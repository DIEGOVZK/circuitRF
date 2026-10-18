# circuitRF SAR-8 -- the material grid openEMS gives MRI_Loop_Coil's own structure, cell by cell, so circuitRF's phantom can be
# compared with the tutorial's voxel for voxel before any run (brief-sar-8 R-sar8-1).
# REFERENCE-GENERATION MATERIAL (brief-sar-1 R-sar1-2's boundary): nothing under src/ or tools/ may import, copy or call this
# file. Run it with any Python that has h5py and numpy (openEMS's venv does):
#
#   python dump_loop_material.py <openEMS executable> <openEMS install prefix> <scratch dir> <openems-ref dir>
#
# The structure is MRI_Loop_Coil/MRI_Loop_Coil.xml exactly as the script wrote it -- its grid, its loop, its DiscMaterial phantom
# (the bundled phantom_head_298MHz.h5 under the install prefix, read by openEMS, never copied) -- with two things added, because
# the XML holds the structure only (sar.md section 3.4): the script's FDTD settings (CellConstantMaterial on, MUR on six faces, its
# Gaussian), and one raw SAR dump (DumpType 29) over the bundled phantom's voxel extent. openEMS runs it for 20 steps: the dump's
# cell data is fixed when the operator is built, so the steps only let it write the file.
#
#   MRI_Loop_Coil/material_raw.h5     the raw dump: /CellData/Conductivity, Density and Volume per cell, /Mesh the cell centres
import os, re, shutil, subprocess, sys
import h5py

openems, prefix, scratch, ref = sys.argv[1], os.path.expanduser(sys.argv[2]), os.path.abspath(sys.argv[3]), os.path.abspath(sys.argv[4])
loop = os.path.join(ref, 'MRI_Loop_Coil')
xml = open(os.path.join(loop, 'MRI_Loop_Coil.xml'), encoding='utf-8').read().replace('${OPENEMS_INSTALL}', prefix)
phantom = re.search(r'<DiscFile [^>]*File="([^"]+)"', xml).group(1)
with h5py.File(phantom, 'r') as f:                       # the voxel extent, metres; the drawing unit is mm
    lo = [float(f['mesh/' + a][0]) * 1e3 for a in 'xyz']
    hi = [float(f['mesh/' + a][-1]) * 1e3 for a in 'xyz']

dump = ('        <DumpBox ID="99" Name="material_raw" DumpType="29" DumpMode="2" FileType="1">\n'
        '            <FD_Samples>298000000</FD_Samples>\n'
        '            <Primitives>\n'
        '                <Box Priority="0">\n'
        f'                    <P1 X="{lo[0]}" Y="{lo[1]}" Z="{lo[2]}" />\n'
        f'                    <P2 X="{hi[0]}" Y="{hi[1]}" Z="{hi[2]}" />\n'
        '                </Box>\n'
        '            </Primitives>\n'
        '        </DumpBox>\n')
i = xml.rfind('</Properties>')
body = (xml[:i] + dump + xml[i:]).split('?>', 1)[1]
head = ('<?xml version="1.0" encoding="UTF-8" standalone="yes" ?>\n<openEMS>\n'
        '  <FDTD NumberOfTimesteps="20" endCriteria="1e-4" CellConstantMaterial="1">\n'
        '    <Excitation Type="0" f0="298e6" fc="300e6" />\n'
        '    <BoundaryCond xmin="MUR" xmax="MUR" ymin="MUR" ymax="MUR" zmin="MUR" zmax="MUR" />\n'
        '  </FDTD>\n')
os.makedirs(scratch, exist_ok=True)
with open(os.path.join(scratch, 'model.xml'), 'w', encoding='utf-8') as f:
    f.write(head + body + '</openEMS>\n')
subprocess.run([openems, 'model.xml'], cwd=scratch, check=True, stdout=subprocess.DEVNULL)
shutil.copy(os.path.join(scratch, 'material_raw.h5'), os.path.join(loop, 'material_raw.h5'))
with h5py.File(os.path.join(loop, 'material_raw.h5'), 'r') as f:
    print('material_raw.h5', f['CellData/Density'].shape, 'cells; tissue cells', int((f['CellData/Density'][()] > 0).sum()))
