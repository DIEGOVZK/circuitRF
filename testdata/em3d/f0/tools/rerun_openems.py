# circuitRF F0 -- re-run every committed openEMS case on another openEMS build and compare its S with
# the committed reference.s2p. SPIKE MATERIAL (brief-sar-1 R-sar1-1): reads the committed case.xml,
# runs it in a scratch directory, post-processes it with the case's own source/postproc.py, and
# writes nothing under testdata.
#   python rerun_openems.py <openEMS executable> <scratch dir> [run ...]
# A run is e.g. A-bondwire/openems/wire-dw6.35; with none given, every one except B's lossless run.
# B-via/openems/dw25-lossless was stopped by hand at 38.28 ns; it is re-run to the timestep its last
# committed probe sample reached, 1,390,346, by rewriting NumberOfTimesteps in the scratch copy only.
import os, re, shutil, subprocess, sys, time
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNS = ['A-bondwire/openems/curve-dw25', 'A-bondwire/openems/wire-dw12.7', 'A-bondwire/openems/wire-dw6.35',
        'A-bondwire/openems/wire-dw4.2333', 'A-bondwire/openems/wire-dw3.175', 'B-via/openems/dw25-lossy']
STOPPED_BY_HAND = {'B-via/openems/dw25-lossless': 1390346}

def read_s2p(path):
    rows = [l.split() for l in open(path) if l.strip() and l[0] not in '!#']
    a = np.array(rows, dtype=float)
    return a[:, 0], a[:, 1] + 1j*a[:, 2], a[:, 3] + 1j*a[:, 4]

def main():
    exe, scratch = sys.argv[1], os.path.abspath(sys.argv[2])
    runs = sys.argv[3:] or RUNS
    for run in runs:
        src = os.path.join(HERE, run)
        dst = os.path.join(scratch, run)
        shutil.rmtree(dst, ignore_errors=True)
        os.makedirs(dst)
        xml = open(os.path.join(src, 'case.xml')).read()
        if run in STOPPED_BY_HAND:
            xml = re.sub(r'NumberOfTimesteps="[^"]*"', f'NumberOfTimesteps="{STOPPED_BY_HAND[run]}"', xml)
        open(os.path.join(dst, 'case.xml'), 'w').write(xml)
        t0 = time.time()
        with open(os.path.join(dst, 'run.log'), 'w') as log:
            rc = subprocess.call([exe, 'case.xml'], cwd=dst, stdout=log, stderr=subprocess.STDOUT)
        wall = time.time() - t0
        steps = re.findall(r'Timestep:\s+(\d+)', open(os.path.join(dst, 'run.log')).read())
        post = os.path.join(HERE, run.split('/')[0], 'openems', 'source', 'postproc.py')
        out = os.path.join(dst, 'reference.s2p')
        subprocess.check_call([sys.executable, post, dst, out, f'rerun of {run}'], stdout=subprocess.DEVNULL)
        f, s11o, s21o = read_s2p(os.path.join(src, 'reference.s2p'))
        _, s11n, s21n = read_s2p(out)
        db = lambda x: 20*np.log10(np.abs(x))
        print(f'{run}: rc {rc}, {wall:.1f} s, last reported step {steps[-1] if steps else "?"}; '
              f'max |dS11| {np.max(abs(s11n-s11o)):.3e}, max |dS21| {np.max(abs(s21n-s21o)):.3e}, '
              f'max d|S21| {np.max(abs(db(s21n)-db(s21o))):.3e} dB, '
              f'max d arg S21 {np.max(abs(np.degrees(np.angle(s21n/s21o)))):.3e} deg', flush=True)

main()
