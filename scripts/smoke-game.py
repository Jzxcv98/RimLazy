"""Windows: exercise the built mod in a disposable 1.7 quicktest game, never in the player's save."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
from xml.etree import ElementTree


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True, type=Path)
    parser.add_argument("--harmony", type=Path, help="Installed Harmony mod (defaults to this Steam library)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--complex-jobs", type=Path, help="Also load the real [FSF] Complex Jobs to check coexistence")
    mode.add_argument("--legacy-save", type=Path, help="Copy and migrate an existing six-tier v0.1 + Complex Jobs QA save")
    mode.add_argument("--changed-splits-save", type=Path, help="Load a native default-layout QA save after enabling all optional columns")
    mode.add_argument("--all-splits", action="store_true", help="Enable every optional native work column in the isolated settings")
    mode.add_argument("--legacy-settings", type=Path, help="Verify the player's matching Complex Jobs XML Extensions settings are imported")
    parser.add_argument("--compare-catalog", type=Path, help="Compare all work-giver assignments with another run's WorkCatalog.json")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    game = args.game.resolve()
    version = (game / "Version.txt").read_text(encoding="utf-8-sig").strip()
    if not version.startswith("1.7.") or not (game / "RimWorld.exe").is_file():
        parser.error("Expected an installed Windows build of RimWorld 1.7")
    harmony = args.harmony or game.parents[1] / "workshop/content/294100/2009463077"
    for path, expected in ((harmony, "brrainz.harmony"), (args.complex_jobs, "frozensnowfox.complexjobs")):
        if path is not None:
            about = path / "About/About.xml"
            if not about.is_file() or (ElementTree.parse(about).findtext("packageId") or "").lower() != expected:
                parser.error(f"Expected {expected} in {path}")
    scenario = next((label for option, label in (
        (args.legacy_save, "migration"), (args.changed_splits_save, "change-layout"), (args.complex_jobs, "coexist"),
        (args.all_splits, "all-splits"), (args.legacy_settings, "legacy-settings")
    ) if option), "native")
    source_save = args.legacy_save or args.changed_splits_save
    if source_save:
        save_root = ElementTree.parse(source_save)
        active = {node.text.casefold() for node in save_root.findall("meta/modIds/li") if node.text}
        if not {"rimlazy.qaprobe", "rimlazy.strategicwork"}.issubset(active) or ("frozensnowfox.complexjobs" in active) != bool(args.legacy_save):
            parser.error("Expected a disposable quicktest save from the requested source layout")
    local = repo / ".local"
    profile = local / f"smoke-{scenario}-{time.time_ns()}"
    profile.mkdir(parents=True)
    if args.legacy_settings:
        # Only copy this mod's paired keys/values. Keep unrelated settings out of QA artifacts.
        tree = ElementTree.parse(args.legacy_settings)
        dictionary = tree.find("ModSettings/dataDict")
        if dictionary is None:
            parser.error("--legacy-settings requires a RimWorld XML Extensions configuration")
        keys = dictionary.find("keys")
        values = dictionary.find("values")
        if keys is None or values is None or len(keys) != len(values):
            parser.error("Malformed XML Extensions settings dictionary")
        for key, value in zip(list(keys), list(values)):
            if not (key.text or "").lower().startswith("frozensnowfox.frozensnowfoxcomplexjobs;"):
                keys.remove(key)
                values.remove(value)
        if not len(keys):
            parser.error("No saved Complex Jobs settings found")
        config = profile / "Config"
        config.mkdir()
        tree.write(config / "Mod_2574315206_XmlMod.xml", encoding="utf-8", xml_declaration=True)
        legacy_priority = args.legacy_settings.parent / "Mod_1994006442_PriorityMaster.xml"
        if not legacy_priority.is_file():
            parser.error("No matching PriorityMaster settings XML next to the player's XML Extensions file")
        shutil.copy2(legacy_priority, config / legacy_priority.name)
    if args.all_splits or args.changed_splits_save:
        config = profile / "Config"
        config.mkdir()
        options = ElementTree.Element("RimLazy")
        for work in ("FSFTaming FSFSlaughter FSFConvert FSFPrisonCare FSFHack FSFTeach FSFSelfTend FSFRescue "
                     "FSFPollution FSFSnow FSFMerge FSFPaint FSFMechanoids FSFPruning FSFSmoothing").split():
            ElementTree.SubElement(options, "enabled").text = work
        ElementTree.ElementTree(options).write(config / "RimLazy.DetailSettings.xml", encoding="utf-8", xml_declaration=True)
    if source_save:
        (profile / "Saves").mkdir()
        shutil.copy2(source_save, profile / "Saves/RimLazy-migration-input.rws")
    if args.changed_splits_save:
        expected = {}
        for pawn in save_root.findall("game/maps/li/things/thing"):
            named = pawn.find("workSettings/rimLazyWorkPrioritiesById")
            if named is None:
                continue
            values = dict(zip((n.text for n in named.findall("keys/li")),
                              (int(n.text) for n in named.findall("values/li")), strict=True))
            if values:
                expected["Thing_" + pawn.findtext("id")] = values
        if not expected:
            parser.error("The source QA save has no named priority snapshots; use a fresh default native run")
        (profile / "named-priorities.json").write_text(json.dumps(expected), encoding="utf-8")
    sdk = Path(os.environ["USERPROFILE"]) / "AppData/Local/RimLazy/dotnet/dotnet.exe"
    env = os.environ | {"DOTNET_CLI_HOME": str(local / "dotnet-cli"), "RIMLAZY_GAME_SMOKE": scenario}
    subprocess.run([str(sdk) if sdk.exists() else "dotnet", "build", "tests/GameSmoke/GameSmoke.csproj", "-c", "Release",
                    f"-p:RimWorldDir={game}", f"-p:HarmonyDir={harmony.resolve()}", "-m:1", "--disable-build-servers", "--nologo"], env=env, cwd=repo, check=True)
    # An independent runtime also keeps temporary test assemblies out of the installed Mods folder.
    shadow = profile / "runtime"
    shadow.mkdir()
    for f in game.iterdir():
        if f.is_file():
            shutil.copy2(f, shadow / f.name)
    for name in ("Data", "Rendering", "vulkan"):
        if (game / name).is_dir():
            shutil.copytree(game / name, shadow / name)
    mods = shadow / "Mods"
    player_mod = mods / "RimLazy"
    shutil.copytree(repo / "Mod", player_mod)
    probe = mods / "RimLazy.GameSmoke"
    shutil.copytree(repo / "tests/GameSmoke/About", probe / "About")
    for target in (player_mod, probe):
        (target / "Assemblies").mkdir()
    for dll in ("StrategicWork.Core.dll", "StrategicWork.RimWorld.dll"):
        shutil.copy2(repo / "src/StrategicWork.RimWorld/bin/Release/net10.0" / dll, player_mod / "Assemblies")
    hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (player_mod / "Assemblies").glob("*.dll")}
    shutil.copy2(repo / "tests/GameSmoke/bin/Release/net10.0/RimLazy.GameSmoke.dll", probe / "Assemblies")
    shutil.copytree(harmony, mods / "Harmony")
    ids = ["brrainz.harmony", "rimlazy.strategicwork", "rimlazy.qaprobe"]
    if args.complex_jobs:
        shutil.copytree(args.complex_jobs, mods / "ComplexJobs")
        ids.append("frozensnowfox.complexjobs")
    print(f"{version}; evidence: {profile}", flush=True)
    result = profile / "RimLazySmoke.json"
    with (profile / "console.log").open("wb") as log:
        process = subprocess.Popen([str(shadow / "RimWorld.exe"), "-Headless", "-quicktest", "-mod=" + ",".join(ids),
                                    "-savedatafolder=" + str(profile)], cwd=shadow, env=env, stdout=log, stderr=log,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            deadline = time.monotonic() + 240
            while not result.exists() and process.poll() is None and time.monotonic() < deadline:
                if log.tell() > 160_000_000:
                    break
                time.sleep(1)
        finally:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=20)
    # Only remove this invocation's newly created runtime, after its process has stopped.
    if shadow.resolve().parent != profile.resolve() or shadow.is_symlink() or shadow.is_junction():
        raise RuntimeError("Unexpected test runtime directory")
    shutil.rmtree(shadow)
    if not result.exists():
        raise SystemExit("Probe did not complete. See console.log in the evidence folder.")
    report = json.loads(result.read_text(encoding="utf-8-sig"))
    report.update(gameBuild=version, assemblySha256=hashes)
    with (profile / "console.log").open(encoding="utf-8", errors="replace") as log_lines:
        errors = [line.strip() for line in log_lines if " ERROR " in line]
    report["gameErrors"] = errors[:10]
    if errors:
        report["ok"] = False
    if args.compare_catalog and report["ok"]:
        expected = json.loads(args.compare_catalog.read_text(encoding="utf-8-sig"))
        actual = json.loads((profile / "WorkCatalog.json").read_text(encoding="utf-8-sig"))
        misassigned = {work: [expected.get(work), actual.get(work)] for work in sorted(expected.keys() | actual.keys())
                       if work not in expected or work not in actual or expected[work] != actual[work]}
        report["comparedWorkGivers"] = len(expected.keys() | actual.keys())
        report["differentWorkGivers"] = misassigned
        report["ok"] = not misassigned
    result.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    if not report["ok"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
