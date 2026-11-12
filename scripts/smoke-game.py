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
    parser.add_argument("--complex-jobs", type=Path, help="Optional installed copy of [FSF] Complex Jobs")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    game = args.game.resolve()
    version = (game / "Version.txt").read_text(encoding="utf-8-sig").strip()
    if not version.startswith("1.7.") or not (game / "RimWorld.exe").is_file():
        parser.error("Expected an installed Windows build of RimWorld 1.7")
    if args.complex_jobs and (ElementTree.parse(args.complex_jobs / "About/About.xml").findtext("packageId") or "").lower() != "frozensnowfox.complexjobs":
        parser.error("--complex-jobs must point to the actual Complex Jobs mod")
    scenario = "complex" if args.complex_jobs else "base"
    local = repo / ".local"
    profile = local / f"smoke-{scenario}-{time.time_ns()}"
    profile.mkdir(parents=True)
    # An independent runtime also keeps temporary test assemblies out of the installed Mods folder.
    shadow = profile / "runtime"
    shadow.mkdir()
    for f in game.iterdir():
        if f.is_file():
            shutil.copy2(f, shadow / f.name)
    for name in ("Data", "Rendering", "vulkan"):
        if (game / name).is_dir():
            shutil.copytree(game / name, shadow / name)
    sdk = Path(os.environ["USERPROFILE"]) / "AppData/Local/RimLazy/dotnet/dotnet.exe"
    env = os.environ | {"DOTNET_CLI_HOME": str(local / "dotnet-cli"), "RIMLAZY_GAME_SMOKE": scenario}
    subprocess.run([str(sdk) if sdk.exists() else "dotnet", "build", "tests/GameSmoke/GameSmoke.csproj", "-c", "Release",
                    f"-p:RimWorldDir={game}", "-m:1", "--disable-build-servers", "--nologo"], env=env, cwd=repo, check=True)
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
    ids = ["rimlazy.strategicwork", "rimlazy.qaprobe"]
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
    result.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    if not report["ok"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
