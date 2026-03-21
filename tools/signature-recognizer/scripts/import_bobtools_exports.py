import argparse
import csv
import json
import shutil
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Iterable
from zipfile import ZipFile


@dataclass(frozen=True)
class SessionContext:
    rsi_handle: str = ""
    ship: str = ""
    star_system: str = ""
    location_path: str = ""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Import BobTools live-scan training zip exports into a consolidated manifest."
    )
    parser.add_argument(
        "inputs",
        nargs="+",
        help="One or more zip files or directories containing zip files exported from BobTools.",
    )
    parser.add_argument(
        "--split",
        default="train",
        choices=["train", "val", "test", "unassigned"],
        help="Default split assigned to imported rows that are usable for training.",
    )
    parser.add_argument(
        "--copy-mode",
        default="skip-existing",
        choices=["skip-existing", "overwrite"],
        help="How to handle already-imported image files.",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    workspace = Path(__file__).resolve().parents[1]
    archives = discover_archives(args.inputs)

    if not archives:
        raise SystemExit("No BobTools export zip files were found in the supplied inputs.")

    imports_root = workspace / "data" / "imports" / "bobtools_exports"
    manifests_root = workspace / "data" / "manifests"
    reports_root = workspace / "outputs" / "reports"
    imports_root.mkdir(parents=True, exist_ok=True)
    manifests_root.mkdir(parents=True, exist_ok=True)
    reports_root.mkdir(parents=True, exist_ok=True)

    all_rows: list[dict[str, str]] = []
    approved_rows: list[dict[str, str]] = []
    summary_lines: list[str] = []
    seen_keys: set[tuple[str, str]] = set()
    imported_count = 0
    approved_count = 0
    skipped_duplicates = 0

    for archive_path in archives:
        archive_rows, archive_summary = import_archive(
            archive_path=archive_path,
            imports_root=imports_root,
            default_split=args.split,
            copy_mode=args.copy_mode,
            seen_keys=seen_keys,
        )
        summary_lines.extend(archive_summary)

        for row in archive_rows:
            all_rows.append(row)
            imported_count += 1
            if row["use_for_training"] == "true":
                approved_rows.append(row)
                approved_count += 1
            if row["status"] == "duplicate-skipped":
                skipped_duplicates += 1

    write_manifest(
        manifests_root / "bobtools_exports.csv",
        all_rows,
        fieldnames=manifest_fieldnames(),
    )
    write_manifest(
        manifests_root / "bobtools_exports_approved.csv",
        approved_rows,
        fieldnames=manifest_fieldnames(),
    )

    summary_path = reports_root / "bobtools_export_ingest_summary.txt"
    summary_path.write_text(
        "\n".join(
            [
                "BobTools export ingest summary",
                "",
                f"Archives processed: {len(archives)}",
                f"Rows imported: {imported_count}",
                f"Rows approved for training: {approved_count}",
                f"Duplicate rows skipped: {skipped_duplicates}",
                "",
                "Per-archive details:",
                *summary_lines,
                "",
                "Next steps:",
                "- Review data/manifests/bobtools_exports_approved.csv for label quality.",
                "- Use preferred_image_path as the current best training image path.",
                "- Promote reviewed rows into your training pipeline or merge them into future dataset splits.",
            ]
        ),
        encoding="utf-8",
    )

    print(f"Processed {len(archives)} archive(s).")
    print(f"Wrote all rows to {manifests_root / 'bobtools_exports.csv'}")
    print(f"Wrote approved rows to {manifests_root / 'bobtools_exports_approved.csv'}")
    print(f"Wrote summary to {summary_path}")


def discover_archives(inputs: Iterable[str]) -> list[Path]:
    archives: list[Path] = []
    for raw_input in inputs:
        input_path = Path(raw_input).expanduser().resolve()
        if input_path.is_file() and input_path.suffix.lower() == ".zip":
            archives.append(input_path)
            continue

        if input_path.is_dir():
            archives.extend(sorted(input_path.rglob("*.zip")))

    return sorted({archive.resolve() for archive in archives})


def import_archive(
    archive_path: Path,
    imports_root: Path,
    default_split: str,
    copy_mode: str,
    seen_keys: set[tuple[str, str]],
) -> tuple[list[dict[str, str]], list[str]]:
    rows: list[dict[str, str]] = []
    summary: list[str] = [f"- {archive_path.name}"]

    with ZipFile(archive_path) as archive:
        members = archive.namelist()
        legacy_csv_member = find_member_by_suffix(members, "scan_training_labels.csv")
        batch_manifest_member = find_member_by_suffix(members, "manifest.csv")

        if legacy_csv_member:
            summary.append("  - layout: legacy-session")
            rows.extend(
                import_legacy_session_rows(
                    archive=archive,
                    archive_path=archive_path,
                    csv_member=legacy_csv_member,
                    imports_root=imports_root,
                    default_split=default_split,
                    copy_mode=copy_mode,
                    seen_keys=seen_keys,
                )
            )
        elif batch_manifest_member:
            summary.append("  - layout: batch-manifest")
            rows.extend(
                import_batch_manifest_rows(
                    archive=archive,
                    archive_path=archive_path,
                    manifest_member=batch_manifest_member,
                    imports_root=imports_root,
                    default_split=default_split,
                    copy_mode=copy_mode,
                    seen_keys=seen_keys,
                )
            )
        else:
            summary.append("  - skipped: missing manifest.csv or scan_training_labels.csv")
            return rows, summary

    imported_rows = sum(1 for row in rows if row["status"] == "imported")
    approved_rows = sum(1 for row in rows if row["use_for_training"] == "true")
    summary.append(f"  - imported rows: {imported_rows}")
    summary.append(f"  - approved rows: {approved_rows}")
    return rows, summary


def import_legacy_session_rows(
    archive: ZipFile,
    archive_path: Path,
    csv_member: str,
    imports_root: Path,
    default_split: str,
    copy_mode: str,
    seen_keys: set[tuple[str, str]],
) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    session_root = PurePosixPath(csv_member).parent
    session_id = session_root.name or archive_path.stem
    session_context = read_session_context(archive, session_root)

    for index, source_row in enumerate(read_csv_rows(archive, csv_member), start=1):
        entry_id = clean_text(source_row.get("entry_id")) or f"{session_id}-{index:04d}"
        dedupe_key = (session_id, entry_id)
        if dedupe_key in seen_keys:
            rows.append(build_duplicate_row(session_id, entry_id, archive_path.name))
            continue

        seen_keys.add(dedupe_key)

        source_relative = clean_text(source_row.get("source_image_relative_path"))
        processed_relative = clean_text(source_row.get("processed_image_relative_path"))
        imported_source = extract_member(
            archive,
            session_root,
            source_relative,
            imports_root / session_id,
            copy_mode,
        )
        imported_processed = extract_member(
            archive,
            session_root,
            processed_relative,
            imports_root / session_id,
            copy_mode,
        )

        rows.append(
            build_import_row(
                session_id=session_id,
                entry_id=entry_id,
                archive_name=archive_path.name,
                row_number=index,
                source_row=source_row,
                imported_source=imported_source,
                imported_processed=imported_processed,
                default_split=default_split,
                session_context=session_context,
                workspace_root=imports_root.parent.parent,
            )
        )

    return rows


def import_batch_manifest_rows(
    archive: ZipFile,
    archive_path: Path,
    manifest_member: str,
    imports_root: Path,
    default_split: str,
    copy_mode: str,
    seen_keys: set[tuple[str, str]],
) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    manifest_root = PurePosixPath(manifest_member).parent

    for index, source_row in enumerate(read_csv_rows(archive, manifest_member), start=1):
        session_id = clean_text(source_row.get("session_id")) or archive_path.stem
        entry_id = clean_text(source_row.get("entry_id")) or f"{session_id}-{index:04d}"
        dedupe_key = (session_id, entry_id)
        if dedupe_key in seen_keys:
            rows.append(build_duplicate_row(session_id, entry_id, archive_path.name))
            continue

        seen_keys.add(dedupe_key)

        source_relative = clean_text(source_row.get("source_image_relative_path"))
        processed_relative = clean_text(source_row.get("processed_image_relative_path"))
        imported_source = extract_member(
            archive,
            manifest_root,
            source_relative,
            imports_root / session_id,
            copy_mode,
        )
        imported_processed = extract_member(
            archive,
            manifest_root,
            processed_relative,
            imports_root / session_id,
            copy_mode,
        )

        rows.append(
            build_import_row(
                session_id=session_id,
                entry_id=entry_id,
                archive_name=archive_path.name,
                row_number=index,
                source_row=source_row,
                imported_source=imported_source,
                imported_processed=imported_processed,
                default_split=default_split,
                session_context=SessionContext(),
                workspace_root=imports_root.parent.parent,
            )
        )

    return rows


def read_session_context(archive: ZipFile, session_root: PurePosixPath) -> SessionContext:
    session_member = str(session_root / "session.json")
    if session_member not in archive.namelist():
        return SessionContext()

    with archive.open(session_member, "r") as stream:
        document = json.load(stream)

    metadata = document.get("metadata") or {}
    return SessionContext(
        rsi_handle=clean_text(metadata.get("rsiHandle")),
        ship=clean_text(metadata.get("ship")),
        star_system=clean_text(metadata.get("starSystem")),
        location_path=clean_text(metadata.get("locationPath")),
    )


def read_csv_rows(archive: ZipFile, member_name: str) -> csv.DictReader:
    with archive.open(member_name, "r") as stream:
        decoded_lines = (line.decode("utf-8-sig") for line in stream)
        yield from csv.DictReader(decoded_lines)


def find_member_by_suffix(members: Iterable[str], suffix: str) -> str | None:
    normalized_suffix = suffix.lower()
    candidates = [member for member in members if member.lower().endswith(normalized_suffix)]
    if not candidates:
        return None
    return sorted(candidates, key=lambda member: (member.count("/"), len(member), member))[0]


def extract_member(
    archive: ZipFile,
    session_root: PurePosixPath,
    relative_member_path: str,
    destination_root: Path,
    copy_mode: str,
) -> Path | None:
    if not relative_member_path:
        return None

    member_path = session_root / PurePosixPath(relative_member_path)
    member_name = str(member_path)
    if member_name not in archive.namelist():
        return None

    safe_relative = Path(*PurePosixPath(relative_member_path).parts)
    destination_path = destination_root / safe_relative
    destination_path.parent.mkdir(parents=True, exist_ok=True)

    if destination_path.exists() and copy_mode == "skip-existing":
        return destination_path

    with archive.open(member_name, "r") as source_stream, destination_path.open("wb") as destination_stream:
        shutil.copyfileobj(source_stream, destination_stream)

    return destination_path


def build_duplicate_row(session_id: str, entry_id: str, archive_name: str) -> dict[str, str]:
    return {
        "id": f"duplicate_{session_id}_{entry_id}",
        "session_id": session_id,
        "entry_id": entry_id,
        "archive_name": archive_name,
        "timestamp_utc": "",
        "capture_profile": "",
        "source_image_path": "",
        "processed_image_path": "",
        "preferred_image_path": "",
        "detected_value": "",
        "correct_value": "",
        "expected_text": "",
        "signature_summary": "",
        "matched_resource": "",
        "is_target_match": "false",
        "ocr_mode": "",
        "ocr_source": "",
        "processing_state": "",
        "selected_targets": "",
        "label_source": "bobtools-user-export",
        "split": "unassigned",
        "use_for_training": "false",
        "rsi_handle": "",
        "ship": "",
        "star_system": "",
        "location_path": "",
        "status": "duplicate-skipped",
        "notes": "Duplicate session_id + entry_id detected in this import run.",
    }


def build_import_row(
    session_id: str,
    entry_id: str,
    archive_name: str,
    row_number: int,
    source_row: dict[str, str],
    imported_source: Path | None,
    imported_processed: Path | None,
    default_split: str,
    session_context: SessionContext,
    workspace_root: Path,
) -> dict[str, str]:
    preferred_image = imported_processed or imported_source
    detected_value = clean_text(source_row.get("detected_value"))
    correct_value = clean_text(source_row.get("correct_value"))
    processing_state = clean_text(source_row.get("processing_state"))
    usable = bool(preferred_image and correct_value and processing_state.lower() == "processed")

    return {
        "id": f"bobtools_{session_id}_{row_number:04d}",
        "session_id": session_id,
        "entry_id": entry_id,
        "archive_name": archive_name,
        "timestamp_utc": clean_text(source_row.get("timestamp_utc")),
        "capture_profile": clean_text(source_row.get("capture_profile")) or "signature",
        "source_image_path": to_workspace_relative(imported_source, workspace_root),
        "processed_image_path": to_workspace_relative(imported_processed, workspace_root),
        "preferred_image_path": to_workspace_relative(preferred_image, workspace_root),
        "detected_value": detected_value,
        "correct_value": correct_value,
        "expected_text": correct_value,
        "signature_summary": clean_text(source_row.get("signature_summary")),
        "matched_resource": clean_text(source_row.get("matched_resource")),
        "is_target_match": normalize_bool(source_row.get("is_target_match")),
        "ocr_mode": clean_text(source_row.get("ocr_mode")),
        "ocr_source": clean_text(source_row.get("ocr_source")),
        "processing_state": processing_state,
        "selected_targets": clean_text(source_row.get("selected_targets")),
        "label_source": "bobtools-user-export",
        "split": default_split if usable else "unassigned",
        "use_for_training": "true" if usable else "false",
        "rsi_handle": session_context.rsi_handle,
        "ship": session_context.ship,
        "star_system": session_context.star_system,
        "location_path": session_context.location_path,
        "status": "imported",
        "notes": build_notes(detected_value, correct_value, preferred_image is not None, processing_state),
    }


def write_manifest(path: Path, rows: list[dict[str, str]], fieldnames: list[str]) -> None:
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def manifest_fieldnames() -> list[str]:
    return [
        "id",
        "session_id",
        "entry_id",
        "archive_name",
        "timestamp_utc",
        "capture_profile",
        "source_image_path",
        "processed_image_path",
        "preferred_image_path",
        "detected_value",
        "correct_value",
        "expected_text",
        "signature_summary",
        "matched_resource",
        "is_target_match",
        "ocr_mode",
        "ocr_source",
        "processing_state",
        "selected_targets",
        "label_source",
        "split",
        "use_for_training",
        "rsi_handle",
        "ship",
        "star_system",
        "location_path",
        "status",
        "notes",
    ]


def build_notes(detected_value: str, correct_value: str, has_image: bool, processing_state: str) -> str:
    notes: list[str] = []
    if not has_image:
        notes.append("Missing source and processed image in the archive.")
    if processing_state.lower() != "processed":
        notes.append(f"Processing state was '{processing_state}' instead of 'Processed'.")
    if not correct_value:
        notes.append("Correct value is empty.")
    elif detected_value != correct_value:
        notes.append("Corrected label differs from OCR output.")
    else:
        notes.append("Correct label matches OCR output.")
    return " | ".join(notes)


def normalize_bool(value: str | None) -> str:
    normalized = clean_text(value).lower()
    return "true" if normalized in {"true", "1", "yes"} else "false"


def clean_text(value: object | None) -> str:
    return str(value or "").strip()


def to_workspace_relative(path: Path | None, workspace_root: Path) -> str:
    if path is None:
        return ""
    return path.resolve().relative_to(workspace_root.resolve()).as_posix()


if __name__ == "__main__":
    main()