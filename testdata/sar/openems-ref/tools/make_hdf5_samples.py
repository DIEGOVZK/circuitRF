# circuitRF SAR-4 -- the HDF5 samples circuitRF's openEMS HDF5 reader (src/RfCore/Hdf5/OpenEmsHdf5.cs) is held to,
# and what openEMS's OWN Python reader (openEMS.utilities.HDF5Dump, openEMS.sar_utils.readSAR) makes of each.
# REFERENCE-GENERATION MATERIAL (brief-sar-1 R-sar1-2's boundary): nothing under src/ or tools/ may import, copy or
# call this file. Run it with openEMS's own venv Python, after run_python_example.py Dipole_SAR ... --keep-raw:
#
#   python make_hdf5_samples.py <sar_calc executable> <scratch dir> <openems-ref dir>
#
# 1. Dipole_SAR/SAR_raw_dump29.h5 (SAR-1's raw DumpType 29 file) as openEMS's reader reads it:
#      Dipole_SAR/raw_e.npy                     HDF5Dump.GetFieldAtIndex(f_idx=0): (3, nx, ny, nz) complex64
#      Dipole_SAR/raw_{conductivity,density,volume}.npy   /CellData/<name>, float32 (nx, ny, nz)
#      Dipole_SAR/raw_mesh_{x,y,z}_m.npy        HDF5Dump.GetMesh()['lines'], metres
# 2. The installed sar_calc on that same raw file, exactly as Dipole_SAR.py asks for it (IEEE_62704, 10 g):
#      Dipole_SAR/SAR_10g.h5. Its array is checked here to equal SAR-1's sar_10g.npy bit for bit, so the pair
#      (raw dump, averaged result) is an exact pair.
# 3. One small openEMS run whose only purpose is frequency-domain E and H field dumps in HDF5 (FileType 1). The
#    physics is trivial and does not matter: a soft E source in a small MUR box beside a lossy block, 600 steps.
#      field_dump/E_fd.h5, field_dump/H_fd.h5   DumpType 10 and 11, DumpMode 1 (node-interpolated), 2 frequencies
#      field_dump/{E,H}_fd_f<k>.npy             HDF5Dump.GetFieldAtIndex(f_idx=k): (3, nx, ny, nz) complex64
#      field_dump/{E,H}_fd_mesh_<a>_m.npy       HDF5Dump.GetMesh()['lines'], metres
#      field_dump/{E,H}_fd_freq_hz.npy          HDF5Dump.frequencies
import os, subprocess, sys
import h5py
import numpy as np
from CSXCAD import ContinuousStructure
from openEMS import openEMS
from openEMS.sar_utils import readSAR
from openEMS.utilities import HDF5Dump

sar_calc, scratch, ref = (os.path.abspath(a) for a in sys.argv[1:4])
os.makedirs(scratch, exist_ok=True)

# 1. the raw dump
dip = os.path.join(ref, 'Dipole_SAR')
raw = os.path.join(dip, 'SAR_raw_dump29.h5')
with HDF5Dump(raw) as d:
    np.save(os.path.join(dip, 'raw_e.npy'), d.GetFieldAtIndex(f_idx=0))
    for a, lines in zip('xyz', d.GetMesh()['lines']):
        np.save(os.path.join(dip, f'raw_mesh_{a}_m.npy'), lines)
    for k in ('Conductivity', 'Density', 'Volume'):
        np.save(os.path.join(dip, f'raw_{k.lower()}.npy'), d.file[f'/CellData/{k}'][()])

# 2. sar_calc on it; exit status 1 is success (CalcFromHDF5's bool)
out = os.path.join(dip, 'SAR_10g.h5')
if os.path.exists(out):
    os.unlink(out)
subprocess.call([sar_calc, '-i', raw, '-o', out, '--method', 'IEEE_62704', '--mass', '10'],
                stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
sar, _, _ = readSAR(out)
assert np.array_equal(sar, np.load(os.path.join(dip, 'sar_10g.npy'))), 'SAR_10g.h5 differs from SAR-1 sar_10g.npy'

# 3. the field dumps
fd_dir = os.path.join(ref, 'field_dump')
os.makedirs(fd_dir, exist_ok=True)
fdtd = openEMS(NrTS=600, EndCriteria=0)
fdtd.SetGaussExcite(1e9, 1e9)
fdtd.SetBoundaryCond(['MUR'] * 6)
csx = ContinuousStructure()
fdtd.SetCSX(csx)
mesh = csx.GetGrid()
mesh.SetDeltaUnit(1e-3)
mesh.SetLines('x', np.linspace(-20, 20, 15))
mesh.SetLines('y', np.linspace(-15, 15, 12))
mesh.SetLines('z', np.linspace(-10, 10, 9))
csx.AddExcitation('src', exc_type=0, exc_val=[0, 0, 1]).AddBox([-1.5, -1.5, -5], [1.5, 1.5, 5])
csx.AddMaterial('block', epsilon=4, kappa=0.5, density=1000).AddBox([5, -10, -8], [18, 10, 8])
freqs = [0.8e9, 1.2e9]
csx.AddDump('E_fd', dump_type=10, dump_mode=1, file_type=1, frequency=freqs).AddBox([-20, -15, -10], [20, 15, 10])
csx.AddDump('H_fd', dump_type=11, dump_mode=1, file_type=1, frequency=freqs).AddBox([-20, -15, -10], [20, 15, 10])
fdtd.Run(os.path.join(scratch, 'field_dump'), cleanup=True, verbose=0)

for name in ('E_fd', 'H_fd'):
    dst = os.path.join(fd_dir, name + '.h5')
    os.replace(os.path.join(scratch, 'field_dump', name + '.h5'), dst)
    with HDF5Dump(dst) as d:
        for k in range(d.GetNumFrequencies()):
            np.save(os.path.join(fd_dir, f'{name}_f{k}.npy'), d.GetFieldAtIndex(f_idx=k))
        for a, lines in zip('xyz', d.GetMesh()['lines']):
            np.save(os.path.join(fd_dir, f'{name}_mesh_{a}_m.npy'), lines)
        np.save(os.path.join(fd_dir, f'{name}_freq_hz.npy'), d.frequencies)
print('ok')
