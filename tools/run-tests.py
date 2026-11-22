"""Runs every test project in the solution with xUnit's own runner and reports exact results. CI uses it.

`dotnet test` goes through the VSTest adapter (xunit.runner.visualstudio 4.0.1), which silently drops a failed test whose
message holds U+0000-U+0008, U+000B, U+000E-U+001F, a lone surrogate or U+FFFE, and exits 0 when those are the only
failures. This script runs each built test assembly with `dotnet exec`, which is xUnit v3's own in-process runner, reads its
XML results and fails when any project fails, crashes or runs no tests.

Build first (or pass --build). Usage, from the repo root:
  python tools/run-tests.py                          # every test project, Release, two projects at a time
  python tools/run-tests.py --build -j 1             # build the solution first, one project at a time
  python tools/run-tests.py -p Egami.Tests -- -class Egami.Tests.Formats.Png.LibpngTests
  python tools/run-tests.py -- -trait- Category=Slow   # everything after -- goes to each runner (filters, -maxThreads ...)

Results go to TestResults/run-tests/<project>.xml (xUnit v3 XML). Projects whose name contains AotSmoke or Fuzz are not
test projects and are left out.
"""
import argparse
import concurrent.futures
import glob
import os
import re
import subprocess
import sys
import threading
import time
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CI = os.environ.get("GITHUB_ACTIONS") == "true"
CONTROL = re.compile("[\x00-\x08\x0b\x0c\x0e-\x1f\x7f\ud800-\udfff\ufffe\uffff]")
print_lock = threading.Lock()


def find_solution():
    found = sorted(glob.glob(os.path.join(ROOT, "*.slnx")))
    if len(found) != 1:
        sys.exit(f"run-tests: expected one .slnx in {ROOT}, found {len(found)}; pass --solution")
    return found[0]


def test_projects(solution):
    """The xUnit test projects in the solution, as (name, csproj path, assembly name)."""
    projects = []
    for node in ET.parse(solution).getroot().iter("Project"):
        path = os.path.join(os.path.dirname(solution), node.get("Path").replace("\\", "/"))
        name = os.path.splitext(os.path.basename(path))[0]
        if "AotSmoke" in name or "Fuzz" in name:
            continue
        with open(path, encoding="utf-8-sig") as f:
            text = f.read()
        if not re.search(r"<IsTestProject>\s*true\s*</IsTestProject>", text, re.I) and "xunit.v3" not in text:
            continue
        match = re.search(r"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>", text)
        projects.append((name, path, match.group(1) if match else name))
    return projects


def assemblies(project, configuration):
    """The built test assemblies of a project (one per target framework)."""
    name, path, assembly = project
    pattern = os.path.join(os.path.dirname(path), "bin", configuration, "*", assembly + ".dll")
    return [(name if i == 0 else f"{name} ({os.path.basename(os.path.dirname(dll))})", dll)
            for i, dll in enumerate(sorted(glob.glob(pattern)))]


def escape(text):
    return CONTROL.sub(lambda m: "\\u%04X" % ord(m.group()), text)


def read_results(path):
    """Counts from an xUnit v3 XML result file, or None when the runner wrote none."""
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError):
        return None
    counts = {"total": 0, "passed": 0, "failed": 0, "skipped": 0, "not-run": 0, "errors": 0}
    for node in root.iter("assembly"):
        for key in counts:
            counts[key] += int(node.get(key) or 0)
    return counts


def run(name, dll, results_dir, runner_args, allow_empty):
    xml_path = os.path.join(results_dir, re.sub(r"[^\w.-]", "_", name) + ".xml")
    if os.path.exists(xml_path):
        os.remove(xml_path)
    command = ["dotnet", "exec", dll, "-noLogo", "-noColor", "-result-xml", xml_path, *runner_args]
    start = time.monotonic()
    proc = subprocess.run(command, cwd=os.path.dirname(dll), stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    seconds = time.monotonic() - start
    output = escape(proc.stdout.decode("utf-8", errors="replace"))
    counts = read_results(xml_path)
    problems = []
    if counts is None:
        problems.append(f"no results (the runner exited with {proc.returncode}; it may have crashed)")
    else:
        if counts["failed"]:
            problems.append(f"{counts['failed']} failed")
        if counts["errors"]:
            problems.append(f"{counts['errors']} errors (cleanup or catastrophic failures)")
        if counts["total"] == 0 and not allow_empty:
            problems.append("no tests ran")
        if proc.returncode != 0 and not problems:
            problems.append(f"the runner exited with {proc.returncode}")
    result = {"name": name, "seconds": seconds, "counts": counts, "problems": problems}
    report(result, output)
    return result


def report(result, output):
    counts = result["counts"] or {}
    line = (f"{result['name']}: {'FAILED' if result['problems'] else 'passed'} - total {counts.get('total', '?')}, "
            f"failed {counts.get('failed', '?')}, skipped {counts.get('skipped', '?')} ({result['seconds']:.0f} s)")
    with print_lock:
        if result["problems"]:
            print(f"===== {line}", flush=True)
            print(output.rstrip(), flush=True)
            if CI:
                print(f"::error title=Tests failed: {result['name']}::{'; '.join(result['problems'])}", flush=True)
        elif CI:
            print(f"::group::{line}\n{output.rstrip()}\n::endgroup::", flush=True)
        else:
            print(line, flush=True)


def main():
    sys.stdout.reconfigure(errors="backslashreplace")  # a Windows console code page cannot show every character
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("-c", "--configuration", default="Release")
    parser.add_argument("-j", "--jobs", type=int, default=2, help="test projects run at once (default 2)")
    parser.add_argument("-p", "--project", action="append", default=[], help="run only this project (repeatable)")
    parser.add_argument("--solution", help="the .slnx (default: the one in the repo root)")
    parser.add_argument("--build", action="store_true", help="dotnet build the solution first")
    parser.add_argument("--results", default=os.path.join(ROOT, "TestResults", "run-tests"), help="folder for the XML results")
    parser.add_argument("--allow-empty", action="store_true", help="a project that runs no tests is not a failure")
    argv = sys.argv[1:]
    runner_args = []
    if "--" in argv:
        split = argv.index("--")
        argv, runner_args = argv[:split], argv[split + 1:]
    args = parser.parse_args(argv)

    solution = os.path.abspath(args.solution) if args.solution else find_solution()
    if args.build:
        build = subprocess.run(["dotnet", "build", solution, "-c", args.configuration])
        if build.returncode != 0:
            return build.returncode

    projects = test_projects(solution)
    unknown = set(args.project) - {p[0] for p in projects}
    if unknown:
        sys.exit(f"run-tests: not a test project in {os.path.basename(solution)}: {', '.join(sorted(unknown))}")
    if args.project:
        projects = [p for p in projects if p[0] in args.project]

    work, missing = [], []
    for project in projects:
        found = assemblies(project, args.configuration)
        if found:
            work.extend(found)
        else:
            missing.append(project[0])
    if missing:
        print(f"run-tests: not built in {args.configuration}: {', '.join(missing)}. Run dotnet build "
              f"{os.path.basename(solution)} -c {args.configuration} first, or pass --build.", flush=True)
        return 1

    os.makedirs(args.results, exist_ok=True)
    work.sort(key=lambda w: -os.path.getsize(w[1]))  # the biggest assemblies usually run longest; start them first
    print(f"run-tests: {len(work)} test assemblies, {args.jobs} at a time, {args.configuration}", flush=True)
    start = time.monotonic()
    with concurrent.futures.ThreadPoolExecutor(max(1, args.jobs)) as pool:
        results = list(pool.map(lambda w: run(w[0], w[1], args.results, runner_args, args.allow_empty), work))

    totals = {key: sum((r["counts"] or {}).get(key, 0) for r in results) for key in ("total", "failed", "skipped")}
    failed = [r for r in results if r["problems"]]
    print("\n===== Summary", flush=True)
    for r in sorted(results, key=lambda r: r["name"]):
        c = r["counts"] or {}
        status = "; ".join(r["problems"]) or "ok"
        print(f"  {r['name']:<40} total {c.get('total', '?'):>6}  failed {c.get('failed', '?'):>4}  "
              f"skipped {c.get('skipped', '?'):>5}  {r['seconds']:6.0f} s  {status}")
    print(f"  {'All':<40} total {totals['total']:>6}  failed {totals['failed']:>4}  skipped {totals['skipped']:>5}  "
          f"{time.monotonic() - start:6.0f} s  {'FAILED: ' + ', '.join(r['name'] for r in failed) if failed else 'ok'}",
          flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
