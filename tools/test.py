"""Runs the tests for one area of Egami, without the slow tests unless you ask for them.

An area is a test project (svg, fonts, drawing, video, video-cli, heic, ...), a folder of tests/Egami.Tests (jxl, png, tiff,
operations, filters, streaming, colors, ...) or core (all of tests/Egami.Tests). An area also runs the docs samples of the same
name (tests/Egami.DocSamples/Jxl.cs for jxl). Anything else is used as a FullyQualifiedName~ filter in tests/Egami.Tests and
tests/Egami.DocSamples. Give several areas separated by commas.

Tests marked [Trait("Category", "Slow")] are left out unless you pass --slow. CI and `dotnet test Egami.slnx` run everything.

Usage (from the repo root):
  python tools/test.py jxl                    # Egami.Tests.Formats.Jxl and DocSamples Jxl, without the slow tests
  python tools/test.py svg,fonts --slow       # the Svg and Fonts test projects, slow tests included
  python tools/test.py png --threads 2        # at most two tests at a time, to keep a shared machine responsive
  python tools/test.py jpeg -- -c Release     # everything after -- goes to dotnet test
  python tools/test.py --list                 # the areas
  python tools/test.py all                    # the whole solution (dotnet test Egami.slnx)
"""
import argparse
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TESTS = os.path.join(ROOT, "tests")
CORE = "tests/Egami.Tests/Egami.Tests.csproj"
DOCS = "tests/Egami.DocSamples/Egami.DocSamples.csproj"
SLOW = "Category!=Slow"


def projects():
    """Maps an area name (svg, video-cli) to its test project, for each tests/Egami.<Name>.Tests."""
    found = {}
    for name in sorted(os.listdir(TESTS)):
        if name.startswith("Egami.") and name.endswith(".Tests") and name != "Egami.Tests":
            area = name[len("Egami."):-len(".Tests")].lower().replace(".", "-")
            found[area] = f"tests/{name}/{name}.csproj"
    return found


def folders():
    """Maps an area name (jxl, filters) to its namespace in tests/Egami.Tests, for each folder up to two levels deep."""
    found = {}
    core = os.path.join(TESTS, "Egami.Tests")
    for parent in [""] + sorted(os.listdir(core)):
        path = os.path.join(core, parent)
        if parent and (not os.path.isdir(path) or parent in ("bin", "obj")):
            continue
        for name in sorted(os.listdir(path)):
            if os.path.isdir(os.path.join(path, name)) and name not in ("bin", "obj"):
                found.setdefault(name.lower(), ".".join(p for p in ("Egami.Tests", parent, name) if p))
    return found


def samples():
    """Maps an area name (jxl) to its class in tests/Egami.DocSamples, for each Name.cs there."""
    folder = os.path.join(TESTS, "Egami.DocSamples")
    return {f[:-3].lower(): f"Egami.DocSamples.{f[:-3]}" for f in os.listdir(folder) if f.endswith(".cs")}


def plan(areas):
    """Returns the (project, filter) runs for the areas; a filter of None runs the whole project."""
    by_project, by_folder, by_sample = projects(), folders(), samples()
    runs = {}

    def add(project, fragment):
        if fragment is None:
            runs[project] = None
        elif runs.get(project, []) is not None:
            runs.setdefault(project, []).append(fragment)

    for area in areas:
        key = area.lower()
        matched = False
        if key == "core":
            add(CORE, None)
            matched = True
        if key in by_project:
            add(by_project[key], None)
            matched = True
        if key in by_folder:
            add(CORE, by_folder[key] + ".")
            matched = True
        if key in by_sample:
            add(DOCS, by_sample[key] + ".")
            matched = True
        if not matched:
            add(CORE, area)
            add(DOCS, area)
    return runs


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("areas", nargs="?", help="comma-separated areas, core, or all")
    parser.add_argument("--slow", action="store_true", help="also run the tests marked Category=Slow")
    parser.add_argument("--threads", type=int, help="the most tests xUnit runs at a time in each project")
    parser.add_argument("--list", action="store_true", help="list the areas and exit")
    parser.add_argument("--dry-run", action="store_true", help="print the dotnet test commands without running them")
    args, extra = parser.parse_known_args()
    if extra and extra[0] == "--":
        extra = extra[1:]

    if args.list or not args.areas:
        print("Test projects:   " + ", ".join(sorted(projects())))
        print("Egami.Tests:     core, " + ", ".join(sorted(folders())))
        print("Docs samples:    " + ", ".join(sorted(samples())))
        print("Whole solution:  all")
        return 0

    runsettings = ["--", f"xUnit.MaxParallelThreads={args.threads}"] if args.threads else []
    if args.areas.lower() == "all":
        commands = [["dotnet", "test", "Egami.slnx", *extra] + ([] if args.slow else ["--filter", SLOW]) + runsettings]
    else:
        commands = []
        for project, fragments in plan([a.strip() for a in args.areas.split(",") if a.strip()]).items():
            terms = [f"FullyQualifiedName~{f}" for f in fragments] if fragments else []
            match = "|".join(terms)
            if not args.slow:
                match = f"({match})&{SLOW}" if len(terms) > 1 else f"{match}&{SLOW}" if terms else SLOW
            command = ["dotnet", "test", project, *extra]
            if match:
                command += ["--filter", match]
            commands.append(command + runsettings)

    status = 0
    for command in commands:
        print("> " + " ".join(f'"{c}"' if any(ch in c for ch in " |&()!") else c for c in command), flush=True)
        if not args.dry_run:
            status = subprocess.call(command, cwd=ROOT) or status
    return status


if __name__ == "__main__":
    sys.exit(main())
