from pathlib import Path


def main() -> None:
    workspace = Path(__file__).resolve().parents[1]
    manifests = workspace / "data" / "manifests"
    print("Signature recognizer training scaffold")
    print(f"Workspace: {workspace}")
    print(f"Expected manifest: {manifests / 'train.csv'}")
    print("Next step: implement dataset loading, model definition, training loop, and checkpoint export.")


if __name__ == "__main__":
    main()