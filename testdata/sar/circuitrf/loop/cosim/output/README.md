# circuitRF OUTPUT for brief-sar-9's co-simulation gate — not a reference

`LoopPorts.s4p` and `fields.json` are what `circuitrf em ../../LoopPorts/3d/LoopPorts.c3d --setup CoSim` wrote (the field files themselves, 201 MB, stay in results/). `cosim.json`, `sar1.json` and the planes are what `circuitrf fields ../LoopCosim.cnl` recombined from them: the schematic's 5.4 pF capacitors and 2.5 Ω feed around the four-port block. CosimLoopCoilTests holds them to SAR-8's lumped-capacitor run in `../../reference-grid/`.
