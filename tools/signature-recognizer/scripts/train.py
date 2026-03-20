import argparse
import csv
import hashlib
import json
from collections import Counter
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class TrainingSample:
    sample_id: str
    source_manifest: str
    session_id: str
    split: str
    image_path: Path
    expected_text: str
    detected_value: str
    label_source: str
    image_hash: str
    notes: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Prepare training manifests for the BobTools signature recognizer."
    )
    parser.add_argument(
        "--manifest",
        action="append",
        default=[],
        help=(
            "Manifest file to import. May be specified multiple times. "
            "Defaults to data/manifests/bobtools_exports_approved.csv when present, "
            "otherwise falls back to data/manifests/train.csv, val.csv, and test.csv."
        ),
    )
    parser.add_argument(
        "--allow-unassigned",
        action="store_true",
        help="Include rows whose split is 'unassigned' by mapping them into the train split.",
    )
    parser.add_argument(
        "--min-label-length",
        type=int,
        default=1,
        help="Minimum non-empty label length required to keep a row.",
    )
    parser.add_argument(
        "--write-json",
        action="store_true",
        help="Also write resolved manifests as JSONL beside the CSV outputs.",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    workspace = Path(__file__).resolve().parents[1]
    manifests_root = workspace / "data" / "manifests"
    reports_root = workspace / "outputs" / "reports"
    reports_root.mkdir(parents=True, exist_ok=True)

    manifest_paths = resolve_manifest_paths(workspace, args.manifest)
    if not manifest_paths:
        raise SystemExit("No manifests were found. Generate starter manifests or import BobTools export zips first.")

    samples: list[TrainingSample] = []
    dropped_rows: list[dict[str, str]] = []

    for manifest_path in manifest_paths:
        loaded_samples, loaded_drops = load_manifest(
            workspace=workspace,
            manifest_path=manifest_path,
            allow_unassigned=args.allow_unassigned,
            min_label_length=args.min_label_length,
        )
        samples.extend(loaded_samples)
        dropped_rows.extend(loaded_drops)

    if not samples:
        raise SystemExit("No training samples survived validation. Review the manifests and labels first.")

    deduped_samples, duplicate_rows = dedupe_samples(samples)
    dropped_rows.extend(duplicate_rows)

    split_samples = {
        split_name: [sample for sample in deduped_samples if sample.split == split_name]
        for split_name in ("train", "val", "test")
    }

    for split_name, split_items in split_samples.items():
        write_resolved_csv(manifests_root / f"resolved_{split_name}.csv", split_items, workspace)
        if args.write_json:
            write_resolved_jsonl(manifests_root / f"resolved_{split_name}.jsonl", split_items, workspace)

    if dropped_rows:
        write_dropped_rows_csv(manifests_root / "resolved_dropped_rows.csv", dropped_rows)

    summary_path = reports_root / "training_manifest_summary.txt"
    summary_path.write_text(
        build_summary_text(
            manifest_paths=manifest_paths,
            samples=deduped_samples,
            split_samples=split_samples,
            dropped_rows=dropped_rows,
        ),
        encoding="utf-8",
    )

    print("Prepared resolved manifests:")
    for split_name in ("train", "val", "test"):
        print(f"- {split_name}: {manifests_root / f'resolved_{split_name}.csv'}")
    if dropped_rows:
        print(f"- dropped rows: {manifests_root / 'resolved_dropped_rows.csv'}")
    print(f"- summary: {summary_path}")
    print("Next step: plug these resolved manifests into the actual model training loop.")


def resolve_manifest_paths(workspace: Path, explicit_manifests: list[str]) -> list[Path]:
    if explicit_manifests:
        return [Path(manifest).expanduser().resolve() for manifest in explicit_manifests]

    manifests_root = workspace / "data" / "manifests"
    preferred = manifests_root / "bobtools_exports_approved.csv"
    if preferred.exists():
        return [preferred]

    fallback = [
        manifests_root / "train.csv",
        manifests_root / "val.csv",
        manifests_root / "test.csv",
    ]
    return [path for path in fallback if path.exists()]


def load_manifest(
    workspace: Path,
    manifest_path: Path,
    allow_unassigned: bool,
    min_label_length: int,
) -> tuple[list[TrainingSample], list[dict[str, str]]]:
    samples: list[TrainingSample] = []
    dropped_rows: list[dict[str, str]] = []

    with manifest_path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        for row_number, row in enumerate(reader, start=2):
            maybe_sample, drop_reason = row_to_sample(
                workspace=workspace,
                manifest_path=manifest_path,
                row=row,
                row_number=row_number,
                allow_unassigned=allow_unassigned,
                min_label_length=min_label_length,
            )
            if maybe_sample is not None:
                samples.append(maybe_sample)
            else:
                dropped_rows.append(
                    {
                        "manifest_path": manifest_path.as_posix(),
                        "row_number": str(row_number),
                        "sample_id": clean_text(row.get("id")),
                        "reason": drop_reason,
                        "expected_text": clean_text(row.get("expected_text") or row.get("correct_value")),
                        "image_path": clean_text(row.get("preferred_image_path") or row.get("crop_image_path") or row.get("raw_image_path")),
                    }
                )

    return samples, dropped_rows


def row_to_sample(
    workspace: Path,
    manifest_path: Path,
    row: dict[str, str],
    row_number: int,
    allow_unassigned: bool,
    min_label_length: int,
) -> tuple[TrainingSample | None, str]:
    split = clean_text(row.get("split")) or infer_split_from_manifest_name(manifest_path)
    if split == "unassigned":
        if not allow_unassigned:
            return None, "split is unassigned"
        split = "train"

    if split not in {"train", "val", "test"}:
        return None, f"unsupported split '{split}'"

    expected_text = normalize_label(clean_text(row.get("expected_text") or row.get("correct_value")))
    if len(expected_text) < min_label_length:
        return None, "label is missing or shorter than minimum length"

    image_path = resolve_image_path(workspace, manifest_path, row)
    if image_path is None:
        return None, "no usable image path was found"
    if not image_path.exists():
        return None, f"image file does not exist: {image_path}"

    image_hash = compute_sha256(image_path)
    sample_id = clean_text(row.get("id")) or f"{manifest_path.stem}_{row_number:04d}"
    source_manifest = manifest_path.name
    session_id = clean_text(row.get("session_id")) or manifest_path.stem
    detected_value = normalize_label(clean_text(row.get("detected_value") or row.get("normalized_text")))
    label_source = clean_text(row.get("label_source")) or "legacy-manifest"
    notes = clean_text(row.get("notes"))

    return (
        TrainingSample(
            sample_id=sample_id,
            source_manifest=source_manifest,
            session_id=session_id,
            split=split,
            image_path=image_path,
            expected_text=expected_text,
            detected_value=detected_value,
            label_source=label_source,
            image_hash=image_hash,
            notes=notes,
        ),
        "",
    )


def infer_split_from_manifest_name(manifest_path: Path) -> str:
    stem = manifest_path.stem.lower()
    if stem.endswith("train"):
        return "train"
    if stem.endswith("val"):
        return "val"
    if stem.endswith("test"):
        return "test"
    return "unassigned"


def resolve_image_path(workspace: Path, manifest_path: Path, row: dict[str, str]) -> Path | None:
    candidates = [
        clean_text(row.get("preferred_image_path")),
        clean_text(row.get("crop_image_path")),
        clean_text(row.get("processed_image_path")),
        clean_text(row.get("raw_image_path")),
        clean_text(row.get("source_image_path")),
    ]
    for candidate in candidates:
        if not candidate:
            continue

        candidate_path = Path(candidate)
        if candidate_path.is_absolute():
            return candidate_path

        workspace_relative = workspace / candidate_path
        if workspace_relative.exists():
            return workspace_relative.resolve()

        manifest_relative = manifest_path.parent / candidate_path
        if manifest_relative.exists():
            return manifest_relative.resolve()

        return workspace_relative.resolve()

    return None


def dedupe_samples(samples: list[TrainingSample]) -> tuple[list[TrainingSample], list[dict[str, str]]]:
    deduped: list[TrainingSample] = []
    dropped: list[dict[str, str]] = []
    seen_hashes: dict[tuple[str, str], TrainingSample] = {}

    for sample in samples:
        dedupe_key = (sample.image_hash, sample.expected_text)
        existing = seen_hashes.get(dedupe_key)
        if existing is None:
            seen_hashes[dedupe_key] = sample
            deduped.append(sample)
            continue

        dropped.append(
            {
                "manifest_path": sample.source_manifest,
                "row_number": "",
                "sample_id": sample.sample_id,
                "reason": f"duplicate image+label of {existing.sample_id}",
                "expected_text": sample.expected_text,
                "image_path": sample.image_path.as_posix(),
            }
        )

    return deduped, dropped


def write_resolved_csv(path: Path, samples: list[TrainingSample], workspace: Path) -> None:
    fieldnames = [
        "id",
        "source_manifest",
        "session_id",
        "split",
        "image_path",
        "expected_text",
        "detected_value",
        "label_source",
        "image_hash",
        "notes",
    ]
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fieldnames)
        writer.writeheader()
        for sample in samples:
            writer.writerow(
                {
                    "id": sample.sample_id,
                    "source_manifest": sample.source_manifest,
                    "session_id": sample.session_id,
                    "split": sample.split,
                    "image_path": to_manifest_image_path(sample.image_path, workspace),
                    "expected_text": sample.expected_text,
                    "detected_value": sample.detected_value,
                    "label_source": sample.label_source,
                    "image_hash": sample.image_hash,
                    "notes": sample.notes,
                }
            )


def write_resolved_jsonl(path: Path, samples: list[TrainingSample], workspace: Path) -> None:
    with path.open("w", encoding="utf-8") as stream:
        for sample in samples:
            stream.write(
                json.dumps(
                    {
                        "id": sample.sample_id,
                        "source_manifest": sample.source_manifest,
                        "session_id": sample.session_id,
                        "split": sample.split,
                        "image_path": to_manifest_image_path(sample.image_path, workspace),
                        "expected_text": sample.expected_text,
                        "detected_value": sample.detected_value,
                        "label_source": sample.label_source,
                        "image_hash": sample.image_hash,
                        "notes": sample.notes,
                    }
                )
                + "\n"
            )


def write_dropped_rows_csv(path: Path, rows: list[dict[str, str]]) -> None:
    fieldnames = ["manifest_path", "row_number", "sample_id", "reason", "expected_text", "image_path"]
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def build_summary_text(
    manifest_paths: list[Path],
    samples: list[TrainingSample],
    split_samples: dict[str, list[TrainingSample]],
    dropped_rows: list[dict[str, str]],
) -> str:
    label_counter = Counter(sample.expected_text for sample in samples)
    manifest_counter = Counter(sample.source_manifest for sample in samples)

    lines = [
        "Signature recognizer training-manifest summary",
        "",
        "Input manifests:",
        *[f"- {path.as_posix()}" for path in manifest_paths],
        "",
        f"Resolved samples: {len(samples)}",
        f"Dropped rows: {len(dropped_rows)}",
        "",
        "Samples by split:",
        *[f"- {split_name}: {len(split_samples[split_name])}" for split_name in ("train", "val", "test")],
        "",
        "Samples by source manifest:",
        *[f"- {manifest_name}: {count}" for manifest_name, count in sorted(manifest_counter.items())],
        "",
        "Top labels:",
        *[f"- {label}: {count}" for label, count in label_counter.most_common(20)],
        "",
        "Next steps:",
        "- Use data/manifests/resolved_train.csv, resolved_val.csv, and resolved_test.csv as the canonical inputs for training code.",
        "- Keep an eye on label imbalance and duplicate capture patterns before model fitting.",
        "- Extend this script next with an actual PyTorch Dataset and training loop once the input pipeline is stable.",
    ]

    return "\n".join(lines)


def normalize_label(value: str) -> str:
    return value.strip()


def to_manifest_image_path(image_path: Path, workspace: Path) -> str:
    resolved_image = image_path.resolve()
    resolved_workspace = workspace.resolve()
    try:
        return resolved_image.relative_to(resolved_workspace).as_posix()
    except ValueError:
        return resolved_image.as_posix()


def compute_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def clean_text(value: object | None) -> str:
    return str(value or "").strip()


if __name__ == "__main__":
    main()