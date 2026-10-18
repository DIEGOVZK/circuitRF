# circuitRF SAR-1 -- openEMS 0.37.0's analytic SAR tests as a check on the installed sar_calc, and their two
# phantoms kept with what the installed sar_calc executable makes of them. REFERENCE-GENERATION MATERIAL
# (brief-sar-1 R-sar1-2): nothing under src/ or tools/ may import, copy or call this file. Run it with
# openEMS's own venv Python:
#
#   python run_analytic.py <openEMS python/Tests dir> <sar_calc executable> <scratch dir> <out dir>
#
# 1. test_sar_analytic.py runs unmodified under unittest; its verbose log is <out dir>/unittest.log.
# 2. Its own writer (_make_raw_sar_hdf5, imported from the test file, not copied) writes its two phantoms:
#    'uniform'  4 x 4 x 4, 1 mm cells, sigma 0.5 S/m, rho 1000 kg/m^3, Ex = 1 V/m peak  (local SAR 2.5e-4 W/kg)
#    'random'  10 x 10 x 10, 2 mm cells, sigma uniform in [0.1, 1.0] from RandomState(42), rho 1000, Ex = 1
#    and the installed sar_calc averages them: 'uniform' as local SAR (--mass 0), 'random' at 1 g by
#    IEEE_62704 (the thread-consistency case). Inputs and outputs are kept as .npy.
#    sar_calc's exit status is CalcFromHDF5's bool, so 1 is success and 0 failure; the file decides.
import os, subprocess, sys, unittest
import numpy as np

tests_dir, sar_calc, scratch, out_dir = (os.path.abspath(a) for a in sys.argv[1:5])
os.makedirs(scratch, exist_ok=True)
os.makedirs(out_dir, exist_ok=True)
sys.path.insert(0, tests_dir)
import test_sar_analytic as t
from openEMS.sar_utils import readSAR
from openEMS.utilities import HDF5Dump

with open(os.path.join(out_dir, 'unittest.log'), 'w') as log:
    result = unittest.TextTestRunner(stream=log, verbosity=2).run(
        unittest.defaultTestLoader.loadTestsFromModule(t))
print('unittest:', 'OK' if result.wasSuccessful() else 'FAILED', result.testsRun, 'tests')

cases = {
    'uniform': (dict(nx=4, ny=4, nz=4, cell_dx=1e-3, conductivity=0.5, density=1000.0, e_field_x=1.0),
                ['--mass', '0']),
    'random':  (dict(nx=10, ny=10, nz=10, cell_dx=2e-3,
                     conductivity=np.random.RandomState(seed=42).uniform(0.1, 1.0, (10, 10, 10)).astype(np.float32),
                     density=1000.0, e_field_x=1.0),
                ['--method', 'IEEE_62704', '--mass', '1']),
}
for name, (kw, args) in cases.items():
    raw = os.path.join(scratch, f'{name}_raw.h5')
    res = os.path.join(scratch, f'{name}_sar.h5')
    for p in (raw, res):
        if os.path.exists(p):
            os.unlink(p)
    t._make_raw_sar_hdf5(raw, **kw)
    with HDF5Dump(raw) as d:
        e = d.GetFieldAtIndex(f_idx=0)
    import h5py
    with h5py.File(raw, 'r') as h:
        for k in ('Conductivity', 'Density', 'Volume'):
            np.save(os.path.join(out_dir, f'{name}_in_{k.lower()}.npy'), h[f'/CellData/{k}'][()])
        for a in 'xyz':
            np.save(os.path.join(out_dir, f'{name}_in_mesh_{a}_m.npy'), h[f'/Mesh/{a}'][()])
    np.save(os.path.join(out_dir, f'{name}_in_e.npy'), e)
    cmd = [sar_calc, '-i', raw, '-o', res] + args
    with open(os.path.join(out_dir, f'{name}_sar_calc.log'), 'w') as log:
        log.write('$ sar_calc ' + ' '.join(['-i', f'{name}_raw.h5', '-o', f'{name}_sar.h5'] + args) + '\n')
        log.flush()
        rc = subprocess.call(cmd, stdout=log, stderr=subprocess.STDOUT)
        log.write(f'exit status {rc}\n')
    # sar_calc returns CalcFromHDF5's bool as its exit status: 1 on success, 0 on failure. Judge by the file.
    if rc != 1 or not os.path.exists(res):
        sys.exit(f'{name}: sar_calc exited {rc} and wrote {"a" if os.path.exists(res) else "no"} result')
    sar, mesh, attrs = readSAR(res)
    np.save(os.path.join(out_dir, f'{name}_out_sar.npy'), sar)
    print(name, 'max SAR', float(np.max(sar)), 'attrs', {k: (v.tolist() if hasattr(v, 'tolist') else v) for k, v in attrs.items()})
