from pathlib import Path


def main() -> None:
    workspace = Path(__file__).resolve().parents[1]
    models = workspace / "outputs" / "models"
    models.mkdir(parents=True, exist_ok=True)
    print("ONNX export scaffold")
    print(f"Workspace: {workspace}")
    print(f"Models directory: {models}")
    print("Next step: load the trained PyTorch model and export signature_recognizer.onnx once quality is acceptable.")


if __name__ == "__main__":
    main()