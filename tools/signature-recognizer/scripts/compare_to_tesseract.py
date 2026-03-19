from pathlib import Path


def main() -> None:
    workspace = Path(__file__).resolve().parents[1]
    baseline = workspace / "outputs" / "reports" / "current_ocr_baseline.csv"
    print("Tesseract comparison scaffold")
    print(f"Workspace: {workspace}")
    print(f"Current BobTools baseline: {baseline}")
    print("Next step: ingest exported manifests and the baseline CSV, then write side-by-side comparison reports.")


if __name__ == "__main__":
    main()