# circuitRF SAR-1 -- turn what run_birdcage.m wrote (raw little-endian doubles plus a manifest) into .npy and a
# summary.json. REFERENCE-GENERATION MATERIAL (brief-sar-1): nothing under src/ or tools/ may import, copy or call it.
#   python birdcage_to_npy.py <out dir>
# Each manifest line is "<name> <complex 0|1> <Octave size...>". Octave writes column-major, so each array is read
# in Fortran order and saved as an ordinary C-order .npy of the same shape; a 1 x N or N x 1 vector becomes 1-D.
# SAR_mesh/H_mesh lines are (r in m, alpha in rad, z in m): the tutorial's grid is cylindrical.
import json, os, sys
import numpy as np

out = os.path.abspath(sys.argv[1])
raw = os.path.join(out, 'raw')
arrays = {}
for line in open(os.path.join(raw, 'manifest.txt')):
    name, cplx, *dims = line.split()
    shape = tuple(int(d) for d in dims)
    n = int(np.prod(shape))
    data = np.fromfile(os.path.join(raw, name + '.bin'), dtype='<f8')
    a = data[:n] + 1j*data[n:2*n] if cplx == '1' else data[:n]
    a = a.reshape(shape, order='F')
    if len(shape) == 2 and 1 in shape:
        a = a.reshape(-1)
    arrays[name] = a
    np.save(os.path.join(out, name + '.npy'), np.ascontiguousarray(a))

scalars = dict(l.split() for l in open(os.path.join(raw, 'scalars.txt')))
f, f0 = arrays['freq_hz'], float(scalars['f0_hz'])
k0 = int(np.argmin(np.abs(f - f0)))
p_acc = arrays['port1_p_acc_w'] + arrays['port2_p_acc_w']
r = arrays['b1_z0_mesh_1']
b1p = np.squeeze(arrays['b1p_z0_t'])
summary = {
    'example': 'MRI_LP_Birdcage',
    'script': 'MRI_LP_Birdcage.m',
    'phantom': "bundled layered cylinder (phantom_body_128MHz.h5), the script's fallback",
    'wall_s': float(scalars['wall_s']),
    'num_cells': int(scalars['num_cells']),
    'f0_hz': f0,
    'freq_nearest_f0_hz': float(f[k0]),
    'accepted_power_both_ports_at_nearest_f0_w': float(p_acc[k0]),
    's11_db_at_nearest_f0': float(20*np.log10(abs(arrays['s11'][k0]))),
    's22_db_at_nearest_f0': float(20*np.log10(abs(arrays['s22'][k0]))),
    'b1p_centre_t': float(np.mean(np.abs(b1p[0, :]))),
    'b1p_centre_note': 'mean |B1+| over the alpha lines at the smallest dumped radius, as the script computed B1+ (not normalised)',
    'smallest_dumped_radius_m': float(r[0]),
    'peak_local_sar_z0_w_per_kg': float(np.max(arrays['sar_z0'])),
}
# The script does not normalise. These two are this converter's, by the tutorials' rule elsewhere (per watt accepted,
# both ports together, at the frequency point nearest f0), so they are named _derived.
summary['b1p_centre_t_per_sqrtw_derived'] = summary['b1p_centre_t'] / np.sqrt(summary['accepted_power_both_ports_at_nearest_f0_w'])
summary['peak_local_sar_z0_w_per_kg_per_w_derived'] = summary['peak_local_sar_z0_w_per_kg'] / summary['accepted_power_both_ports_at_nearest_f0_w']
s11 = arrays['s11']
k = int(np.argmin(np.abs(s11)))
summary['s11_min_db'] = float(20*np.log10(abs(s11[k])))
summary['s11_min_hz'] = float(f[k])
summary['s11_note'] = 'both ports are driven, so S11 and S22 include the other port\'s coupling and may exceed 0 dB (the script says so)'
with open(os.path.join(out, 'summary.json'), 'w') as fh:
    json.dump(summary, fh, indent=2, sort_keys=True)
    fh.write('\n')
print(json.dumps(summary, indent=2, sort_keys=True))
