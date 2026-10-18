% circuitRF SAR-1 -- run openEMS 0.37.0's Octave tutorial MRI_LP_Birdcage.m UNMODIFIED and keep what it computed.
% REFERENCE-GENERATION MATERIAL (brief-sar-1 R-sar1-2, O5): nothing under src/ or tools/ may import, copy or call
% this file (the GPL boundary of testdata/em3d/f0/README.md). Octave is a developer-machine tool only.
%
%   octave --no-gui --norc run_birdcage.m <MRI_LP_Birdcage.m> <sim root> <out dir> <openEMS matlab dir> \
%          <CSXCAD matlab dir> <openEMS executable> [setup-only]
%
% The tutorial is copied byte for byte into <sim root> and run there with run(), so its Sim_Path ('tmp_' plus its
% own file name) and its pwd-relative voxel-model path land in <sim root>; no voxel body model is there, so it takes
% its bundled layered-cylinder phantom. Only what stops a headless run is replaced:
%   - CSXGeomPlot (the AppCSXCAD geometry viewer) is octave-stubs/CSXGeomPlot.m, first on the path, which opens nothing;
%   - figures are invisible, and printed to <out dir>/figures/ when the script has finished.
% No parameter of the script is changed. 'setup-only' puts octave-setup-only/RunOpenEMS.m first on the path instead
% of the real one: openEMS sets the problem up and stops, which measures the run before it is committed to.
% Afterwards the script's own workspace is read and every array written as raw little-endian doubles (column-major,
% real then imaginary) with a manifest, which birdcage_to_npy.py turns into .npy.
global CRF_ARGS CRF_OPENEMS CRF_T0
CRF_ARGS = argv();
here = fileparts(mfilename('fullpath'));
addpath(CRF_ARGS{4});
addpath(CRF_ARGS{5});
addpath(fullfile(here, 'octave-stubs'));
if numel(CRF_ARGS) >= 7 && strcmp(CRF_ARGS{7}, 'setup-only')
    addpath(fullfile(here, 'octave-setup-only'));
end
CRF_OPENEMS = CRF_ARGS{6};
set(0, 'defaultfigurevisible', 'off');

[~, name, ext] = fileparts(CRF_ARGS{1});
if ~exist(CRF_ARGS{2}, 'dir'), mkdir(CRF_ARGS{2}); end
if ~exist(fullfile(CRF_ARGS{3}, 'figures'), 'dir'), mkdir(fullfile(CRF_ARGS{3}, 'figures')); end
copyfile(CRF_ARGS{1}, fullfile(CRF_ARGS{2}, [name ext]));
CRF_T0 = tic;
run(fullfile(CRF_ARGS{2}, [name ext]));

% ---- the tutorial has finished; its variables are in this workspace (its own `clear` ran at its top) ----
global CRF_ARGS CRF_T0
wall = toc(CRF_T0);
out = CRF_ARGS{3};
printf('[wrapper] script finished in %.1f s\n', wall);

figs = sort(get(0, 'children'));
for n = 1:numel(figs)
    try
        print(figs(n), fullfile(out, 'figures', sprintf('figure%d.png', n)), '-dpng', '-r80');
    catch err
        printf('[wrapper] figure %d not printed: %s\n', n, err.message);
    end
end

copyfile(fullfile(CRF_ARGS{2}, Sim_Path, 'BirdCage.xml'), fullfile(out, 'BirdCage.xml'));

arrays = struct();
arrays.freq_hz = freq;
for p = 1:numel(port)
    arrays.(sprintf('port%d_uf_inc', p)) = port{p}.uf.inc;
    arrays.(sprintf('port%d_uf_ref', p)) = port{p}.uf.ref;
    arrays.(sprintf('port%d_uf_tot', p)) = port{p}.uf.tot;
    arrays.(sprintf('port%d_if_tot', p)) = port{p}.if.tot;
    arrays.(sprintf('port%d_p_acc_w', p)) = port{p}.P_acc;
end
arrays.s11 = s11;
arrays.s22 = s22;
arrays.sar_z0 = SAR;                       % local SAR on the z = 0 plane, (r, alpha), as the script read it
for k = 1:numel(SAR_mesh.lines)
    arrays.(sprintf('sar_z0_mesh_%d', k)) = SAR_mesh.lines{k};
end
arrays.b1p_z0_t = B1p;                     % B1+ = (Bx + jBy)/2 in T, exactly as the script computed it
arrays.b1m_z0_t = B1m;
for k = 1:numel(H_mesh.lines)
    arrays.(sprintf('b1_z0_mesh_%d', k)) = H_mesh.lines{k};
end

fid = fopen(fullfile(out, 'raw', 'manifest.txt'), 'w');
if fid < 0
    mkdir(fullfile(out, 'raw'));
    fid = fopen(fullfile(out, 'raw', 'manifest.txt'), 'w');
end
names = fieldnames(arrays);
for n = 1:numel(names)
    a = double(arrays.(names{n}));
    f = fopen(fullfile(out, 'raw', [names{n} '.bin']), 'w', 'ieee-le');
    fwrite(f, real(a(:)), 'double');
    if ~isreal(a), fwrite(f, imag(a(:)), 'double'); end
    fclose(f);
    fprintf(fid, '%s %d %s\n', names{n}, ~isreal(a), sprintf('%d ', size(a)));
end
fclose(fid);
fid = fopen(fullfile(out, 'raw', 'scalars.txt'), 'w');
fprintf(fid, 'wall_s %.1f\nf0_hz %.17g\nnum_cells %d\n', wall, f0, numCells);
fclose(fid);
printf('[wrapper] wrote %d arrays\n', numel(names));
