# circuitRF SAR-8b -- the material grid openEMS gives MRI_LP_Birdcage's own structure, cell by cell, so circuitRF's phantom can be
# compared with the tutorial's voxel for voxel before any run (brief-sar-8b, the way SAR-8 compared the loop coil's).
# REFERENCE-GENERATION MATERIAL (brief-sar-1 R-sar1-2's boundary): nothing under src/ or tools/ may import, copy or call this
# file. Run it with any Python that has h5py and numpy (openEMS's venv does):
#
#   python dump_birdcage_material.py <openEMS executable> <openEMS install prefix> <scratch dir> <openems-ref dir>
#
# The structure is MRI_LP_Birdcage/BirdCage.xml exactly as the script wrote it with WriteOpenEMS -- its FDTD settings, its
# cylindrical grid and MultiGrid, its birdcage, its DiscMaterial phantom (the bundled phantom_body_128MHz.h5 under the install
# prefix, read by openEMS, never copied) -- with two changes: 20 steps instead of the script's limit, and one raw SAR dump
# (DumpType 29) over rho 0 to 120 mm, the whole circle and z -130 to 130 mm (the phantom's extent). The dump's cell data is fixed
# when the operator is built, so the steps only let it write the file.
#
#   MRI_LP_Birdcage/material_raw.h5     the raw dump: /CellData/Conductivity, Density and Volume per cell, /Mesh the cell centres
import os, re, shutil, subprocess, sys
import h5py

openems, prefix, scratch, ref = sys.argv[1], os.path.expanduser(sys.argv[2]), os.path.abspath(sys.argv[3]), os.path.abspath(sys.argv[4])
cage = os.path.join(ref, 'MRI_LP_Birdcage')
xml = open(os.path.join(cage, 'BirdCage.xml'), encoding='utf-8').read().replace('${OPENEMS_INSTALL}', prefix)
xml = re.sub(r'NumberOfTimesteps="\d+"', 'NumberOfTimesteps="20"', xml, count=1)
alpha = re.search(r'<YLines>([^<]+)</YLines>', xml).group(1).split(',')
a0, a1 = alpha[0].strip(), alpha[-1].strip()
dump = ('      <DumpBox Name="material_raw" DumpType="29" DumpMode="2" FileType="1">\n'
        '        <FD_Samples>128000000</FD_Samples>\n'
        '        <Primitives>\n'
        '          <Box Priority="0">\n'
        f'            <P1 X="0" Y="{a0}" Z="-130" />\n'
        f'            <P2 X="120" Y="{a1}" Z="130" />\n'
        '          </Box>\n'
        '        </Primitives>\n'
        '      </DumpBox>\n')
i = xml.rfind('</Properties>')
os.makedirs(scratch, exist_ok=True)
with open(os.path.join(scratch, 'model.xml'), 'w', encoding='utf-8') as f:
    f.write(xml[:i] + dump + xml[i:])
subprocess.run([openems, 'model.xml'], cwd=scratch, check=True, stdout=subprocess.DEVNULL)
shutil.copy(os.path.join(scratch, 'material_raw.h5'), os.path.join(cage, 'material_raw.h5'))
with h5py.File(os.path.join(cage, 'material_raw.h5'), 'r') as f:
    print('material_raw.h5', f['CellData/Density'].shape, 'cells; tissue cells', int((f['CellData/Density'][()] > 0).sum()))
