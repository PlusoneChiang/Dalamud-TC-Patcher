import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import zipfile


def validate_tag(tag, manifest):
    number = r"(?:0|[1-9][0-9]*)"
    match = re.fullmatch(
        rf"v(?P<upstream>{number}(?:\.{number}){{3}})-tc\.[1-9][0-9]*(?:-rc\.[1-9][0-9]*)?",
        tag,
    )
    if not match:
        raise ValueError("Expected v<four-part upstream version>-tc.<revision>[-rc.<number>]; revision and RC numbers start at 1.")
    if tag != "v" + manifest["id"]:
        raise ValueError("Tag must equal 'v' followed by manifest.id.")
    if match["upstream"] != manifest["upstreamVersion"]:
        raise ValueError("Tag upstream version must match manifest.upstreamVersion.")


def package(root, tag, commit):
    manifest = json.loads((root / "manifests/dalamud.json").read_text(encoding="utf-8"))
    validate_tag(tag, manifest)
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("A full Patcher commit SHA is required.")
    source = root / "artifacts/patched-dalamud"
    report_path = source / "build-report.json"
    report = json.loads(report_path.read_text(encoding="utf-8"))
    for field in ("id", "upstreamVersion", "upstreamSourceCommit", "gameVersion"):
        if report[field] != manifest[field]:
            raise ValueError(f"Build report {field} does not match the manifest; rebuild the package.")
    if report["inputSha256"] != manifest["upstream"]["sha256"]:
        raise ValueError("Build report upstream hash does not match the manifest; rebuild the package.")
    report["releaseTag"] = tag
    report["patcherSourceCommit"] = commit
    report_text = json.dumps(report, ensure_ascii=False, indent=2) + "\n"

    output = root / "artifacts/release"
    output.mkdir(exist_ok=False)
    archive_path = output / f"dalamud-{manifest['id']}.zip"
    with zipfile.ZipFile(archive_path, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(source.rglob("*")):
            if path.is_symlink():
                raise ValueError(f"Release directory must not contain symlinks: {path}")
            if path.is_file():
                name = path.relative_to(source).as_posix()
                if path == report_path:
                    archive.writestr(name, report_text)
                else:
                    archive.write(path, name)
    digest = hashlib.sha256()
    with archive_path.open("rb") as archive:
        for block in iter(lambda: archive.read(1024 * 1024), b""):
            digest.update(block)
    (output / f"{archive_path.name}.sha256").write_text(
        f"{digest.hexdigest()}  {archive_path.name}\n", encoding="utf-8"
    )
    (output / "build-report.json").write_text(report_text, encoding="utf-8")
    notes = (
        f"TC 套件版本：`{manifest['id']}`\n\n"
        f"- 官方 Dalamud：`{manifest['upstreamVersion']}`\n"
        f"- 官方原始碼 commit：`{manifest['upstreamSourceCommit']}`\n"
        f"- 遊戲版本：`{manifest['gameVersion']}`\n"
        f"- Patcher commit：`{commit}`\n\n"
        "ZIP 內直接包含套件檔案；下載後請先以附帶的 `.sha256` 驗證完整性。\n"
    )
    if "-rc." in tag:
        notes += "\n此為候選版本，供遊戲內相容性測試，尚未列為正式版。\n"
    (output / "release-notes.md").write_text(notes, encoding="utf-8")
    return archive_path


def main():
    parser = argparse.ArgumentParser(description="Validate TC release versions and package existing build output.")
    commands = parser.add_subparsers(dest="command", required=True)
    validate = commands.add_parser("validate", help="Validate the tag against the manifest.")
    validate.add_argument("tag")
    build = commands.add_parser("package", help="Package artifacts/patched-dalamud without overwriting an existing release directory.")
    build.add_argument("tag")
    build.add_argument("commit")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    try:
        if args.command == "validate":
            manifest = json.loads((root / "manifests/dalamud.json").read_text(encoding="utf-8"))
            validate_tag(args.tag, manifest)
            print(f"Release version validated: {args.tag}")
        else:
            print(f"Release archive created: {package(root, args.tag, args.commit)}")
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        print(f"Release failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
