import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile


spec = importlib.util.spec_from_file_location("release", Path(__file__).resolve().parents[1] / "scripts/release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {
            "id": "13.0.0.16-tc.1-rc.1",
            "upstreamVersion": "13.0.0.16",
            "upstreamSourceCommit": "a" * 40,
            "gameVersion": "2026.07.22.0000.0000",
            "upstream": {"sha256": "b" * 64},
        }
        self.tag = "v" + self.manifest["id"]

    def test_accepts_stable_and_candidate_tags(self):
        for version in ("13.0.0.16-tc.1", "13.0.0.16-tc.12", "13.0.0.16-tc.1-rc.2"):
            with self.subTest(version=version):
                manifest = dict(self.manifest, id=version)
                release.validate_tag("v" + version, manifest)

    def test_rejects_invalid_tags_even_when_manifest_id_matches(self):
        for version in (
            "13.0.0-tc.1", "13.0.0.16-tc.0", "13.0.0.16-tc.01",
            "13.0.0.16-tc.1-rc.0", "13.0.0.16-tc.1-rc.01",
            "13.0.0.16-tc.1+build.2", "13.0.0.16-tc.1-rc.1/extra",
            "13.0.0.016-tc.1", "13.0.0.16-tc.1\n",
        ):
            with self.subTest(version=version), self.assertRaises(ValueError):
                release.validate_tag("v" + version, dict(self.manifest, id=version))

    def test_rejects_manifest_mismatches(self):
        for field, value in (("id", "13.0.0.16-tc.2"), ("upstreamVersion", "13.0.0.17")):
            with self.subTest(field=field), self.assertRaises(ValueError):
                release.validate_tag(self.tag, dict(self.manifest, **{field: value}))

    def test_packages_root_files_provenance_checksum_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "manifests").mkdir()
            (root / "manifests/dalamud.json").write_text(json.dumps(self.manifest), encoding="utf-8")
            source = root / "artifacts/patched-dalamud"
            (source / "data").mkdir(parents=True)
            (source / "Dalamud.dll").write_bytes(b"fixture")
            (source / "data/file.txt").write_text("測試", encoding="utf-8")
            report = {key: self.manifest[key] for key in ("id", "upstreamVersion", "upstreamSourceCommit", "gameVersion")}
            report["inputSha256"] = self.manifest["upstream"]["sha256"]
            report["patcherBuild"] = {"sdkVersion": "9.0.311"}
            report["patcherRuntime"] = {"version": "9.0.13", "osDescription": "Linux", "processArchitecture": "X64"}
            report["runtimeRequirements"] = {"Dalamud.runtimeconfig.json": {"frameworks": [
                {"name": "Microsoft.NETCore.App", "version": "9.0.3"},
                {"name": "Microsoft.WindowsDesktop.App", "version": "9.0.3"},
            ], "rollForward": "LatestMinor"}}
            original_report = json.dumps(report)
            (source / "versionInfo.json").write_text(original_report, encoding="utf-8")
            archive = release.package(root, self.tag, "c" * 40)
            checksum = archive.with_name(archive.name + ".sha256").read_text(encoding="utf-8")
            self.assertEqual(f"{hashlib.sha256(archive.read_bytes()).hexdigest()}  {archive.name}\n", checksum)
            report = json.loads((archive.parent / "versionInfo.json").read_text(encoding="utf-8"))
            self.assertEqual(self.tag, report["releaseTag"])
            self.assertEqual("c" * 40, report["patcherSourceCommit"])
            original = json.loads(original_report)
            for field in ("patcherBuild", "patcherRuntime", "runtimeRequirements"):
                self.assertEqual(original[field], report[field])
            with zipfile.ZipFile(archive) as package:
                self.assertEqual({"Dalamud.dll", "data/file.txt", "versionInfo.json"}, set(package.namelist()))
                self.assertEqual(report, json.loads(package.read("versionInfo.json")))
                self.assertEqual(b"fixture", package.read("Dalamud.dll"))
            self.assertEqual(original_report, (source / "versionInfo.json").read_text(encoding="utf-8"))
            self.assertIn("候選版本", (archive.parent / "release-notes.md").read_text(encoding="utf-8"))
            with self.assertRaises(FileExistsError):
                release.package(root, self.tag, "c" * 40)
            for field in ("id", "upstreamVersion", "upstreamSourceCommit", "gameVersion", "inputSha256"):
                with self.subTest(field=field):
                    bad_report = dict(report, **{field: "mismatch"})
                    (source / "versionInfo.json").write_text(json.dumps(bad_report), encoding="utf-8")
                    with self.assertRaisesRegex(ValueError, "does not match"):
                        release.package(root, self.tag, "c" * 40)
            with self.assertRaisesRegex(ValueError, "commit SHA"):
                release.package(root, self.tag, "not-a-commit")


if __name__ == "__main__":
    unittest.main()
