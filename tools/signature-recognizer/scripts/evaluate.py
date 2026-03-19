from pathlib import Path


def main() -> None:
    workspace = Path(__file__).resolve().parents[1]
    reports = workspace / "outputs" / "reports"
    reports.mkdir(parents=True, exist_ok=True)
    print("Signature recognizer evaluation scaffold")
    print(f"Workspace: {workspace}")
    print(f"Reports directory: {reports}")
    print("Next step: load a trained model, score the frozen test set, and write eval.csv plus summary.json.")


if __name__ == "__main__":
    main()