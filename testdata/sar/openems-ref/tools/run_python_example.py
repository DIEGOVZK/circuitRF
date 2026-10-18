# circuitRF SAR-1 -- run one of openEMS 0.37.0's own Python tutorials UNMODIFIED and keep what it computed.
# REFERENCE-GENERATION MATERIAL (brief-sar-1 R-sar1-2): nothing under src/ or tools/ may import, copy or
# call this file (the GPL boundary of testdata/em3d/f0/README.md). Run it with openEMS's own venv Python:
#
#   python run_python_example.py <Dipole_SAR|MRI_Loop_Coil> <tutorial .py> <sim root> <out dir> [--keep-raw]
#                                [--exact-endcriteria]
#
# The tutorial runs through runpy with its physics untouched. Only what stops a headless run is replaced:
#   - matplotlib draws on the non-interactive Agg backend, and plt.show() saves every open figure to
#     <out dir>/figures/ instead of opening a window;
#   - os.system() of the geometry viewer (AppCSXCAD) is skipped; any other os.system() call runs as written;
#   - tempfile.gettempdir() is <sim root>, so the script's Sim_Path lands there, and the working directory
#     is <sim root>, so MRI_Loop_Coil finds no voxel body model there and takes its bundled ellipsoid phantom.
# --exact-endcriteria (R-sar1-5 only, never for a committed reference) passes openEMS's own option of that name
# through FDTD.Run's keyword arguments, which the Python interface hands to the engine as command-line options:
# the energy end criterion is then evaluated every Nyquist interval rather than every few seconds of wall-clock
# time, so the run stops at the same timestep however loaded the machine is.
# No parameter of the script is changed. Afterwards the script's own module globals are read back and
# written out with numpy and openEMS's own readers (readSAR, HDF5Dump), so nothing here depends on
# circuitRF's HDF5 reader.
import json, os, runpy, shutil, sys, tempfile, time

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot
import matplotlib.pylab
import numpy as np

example, script, sim_root, out_dir = (os.path.abspath(a) if i else a for i, a in enumerate(sys.argv[1:5]))
keep_raw = '--keep-raw' in sys.argv[5:]
exact_endcriteria = '--exact-endcriteria' in sys.argv[5:]
os.makedirs(sim_root, exist_ok=True)
os.makedirs(os.path.join(out_dir, 'figures'), exist_ok=True)

saved = []
def save_figures(*_, **__):
    for n in matplotlib.pyplot.get_fignums():
        path = os.path.join(out_dir, 'figures', f'figure{n}.png')
        matplotlib.pyplot.figure(n).savefig(path, dpi=80)
        saved.append(os.path.basename(path))
    matplotlib.pyplot.close('all')
matplotlib.pyplot.show = save_figures
matplotlib.pylab.show = save_figures

_system = os.system
def system(cmd):
    if 'AppCSXCAD' in cmd:
        print(f'[wrapper] geometry viewer skipped: {cmd}', flush=True)
        return 0
    return _system(cmd)
os.system = system

if exact_endcriteria:
    import openEMS
    class _ExactEndCriteria(openEMS.openEMS):
        def Run(self, sim_path, cleanup=False, setup_only=False, **kw):
            return super().Run(sim_path, cleanup=cleanup, setup_only=setup_only, exact_endcriteria=True, **kw)
    openEMS.openEMS = _ExactEndCriteria

tempfile.tempdir = sim_root
os.chdir(sim_root)

t0 = time.time()
g = runpy.run_path(script, run_name='__main__')
wall = time.time() - t0
print(f'[wrapper] script finished in {wall:.1f} s', flush=True)

from openEMS.sar_utils import readSAR
from openEMS.utilities import HDF5Dump

sim = g['Sim_Path']
def npy(name, a):
    np.save(os.path.join(out_dir, name), np.asarray(a))

def port_results(freq):
    p = g['port']
    npy('freq_hz.npy', freq)
    npy('s11.npy', p.uf_ref / p.uf_inc)
    npy('zin_ohm.npy', p.uf_tot / p.if_tot)
    npy('p_acc_w.npy', p.P_acc)

def sar_file(src, stem):
    """A SAR result file the script reads: its array and mesh lines (metres) as .npy, its attributes returned."""
    sar, mesh, attrs = readSAR(src)
    npy(f'{stem}.npy', sar)
    for axis, lines in zip('xyz', mesh):
        npy(f'{stem}_mesh_{axis}_m.npy', lines)
    return {k: (v.tolist() if hasattr(v, 'tolist') else v) for k, v in attrs.items()}

summary = {'example': example, 'script': os.path.basename(script), 'wall_s': round(wall, 1), 'figures': saved,
           'exact_endcriteria': exact_endcriteria}

if example == 'Dipole_SAR':
    f0 = float(g['f0'])
    port_results(g['f'])
    shutil.copy(os.path.join(sim, 'sar_dipole.xml'), os.path.join(out_dir, 'sar_dipole.xml'))
    summary['sar_10g'] = sar_file(os.path.join(sim, 'SAR_10g.h5'), 'sar_10g')
    pin, prad, ptotal, mass = float(g['Pin_f0']), float(g['nf2ff_res'].Prad[0]), float(g['ptotal']), float(g['mass'])
    zin_f0 = complex(np.interp(f0, g['f'], g['Zin'].real) + 1j*np.interp(f0, g['f'], g['Zin'].imag))
    summary.update({
        'f0_hz': f0,
        'accepted_power_w': pin,
        'radiated_power_w': prad,
        'absorbed_power_w': ptotal,
        'phantom_mass_kg': mass,
        'psSAR10g_w_per_kg_per_w': float(g['max_sar']) / pin,
        'whole_phantom_sar_w_per_kg_per_w': ptotal / pin / mass,
        'power_budget_percent': 100 * (prad + ptotal) / pin,
        'zin_f0_ohm': [zin_f0.real, zin_f0.imag],
    })
    raw = os.path.join(sim, 'SAR.h5')
    summary['raw_dump_bytes'] = os.path.getsize(raw)
    if keep_raw:
        shutil.copy(raw, os.path.join(out_dir, 'SAR_raw_dump29.h5'))

elif example == 'MRI_Loop_Coil':
    f0 = float(g['f0'])
    port_results(g['freq'])
    shutil.copy(os.path.join(sim, 'MRI_Loop_Coil.xml'), os.path.join(out_dir, 'MRI_Loop_Coil.xml'))
    summary['sar_xy'] = sar_file(os.path.join(sim, 'SAR_xy.h5'), 'sar_xy')
    summary['sar_xz'] = sar_file(os.path.join(sim, 'SAR_xz.h5'), 'sar_xz')
    # B1+/- per sqrt(W) accepted, exactly as the script computed them, with the dump's own mesh lines
    for plane, (ny, axes) in {'xy': ('z', 'xy'), 'xz': ('y', 'xz')}.items():
        with HDF5Dump(os.path.join(sim, f'Hf_{plane}.h5')) as dump:
            dump.SetPlane(ny, pos=0)
            lines = dump.GetMesh(region=True)['lines']
        for a, l in zip(axes, (lines[0], lines[1] if plane == 'xy' else lines[2])):
            npy(f'b1_{plane}_mesh_{a}_m.npy', l)
        npy(f'b1p_{plane}_t_per_sqrtw.npy', g[f'B1p_{plane}'])
        npy(f'b1m_{plane}_t_per_sqrtw.npy', g[f'B1m_{plane}'])
    s11 = g['s11']
    k = int(np.argmin(np.abs(s11)))
    pin = float(g['P0_in'])
    zin_f0 = complex(np.interp(f0, g['freq'], g['Zin'].real) + 1j*np.interp(f0, g['freq'], g['Zin'].imag))
    summary.update({
        'f0_hz': f0,
        'phantom': 'bundled ellipsoid head phantom (phantom_head_298MHz.h5), the script\'s fallback',
        'accepted_power_w': pin,
        's11_min_hz': float(g['freq'][k]),
        's11_min_db': float(20*np.log10(np.abs(s11[k]))),
        'yin_f0_s': [(1/zin_f0).real, (1/zin_f0).imag],
        'peak_local_sar_xy_w_per_kg_per_w': float(np.max(g['sar_xy'])),
        'peak_local_sar_xz_w_per_kg_per_w': float(np.max(g['sar_xz'])),
    })
else:
    sys.exit(f'unknown example {example}')

with open(os.path.join(out_dir, 'summary.json'), 'w') as fh:
    json.dump(summary, fh, indent=2, sort_keys=True)
    fh.write('\n')
print(json.dumps(summary, indent=2, sort_keys=True))
