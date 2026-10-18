function RunOpenEMS(Sim_Path, Sim_File, opts, Settings)
% circuitRF SAR-1 -- run_birdcage.m's setup-only mode: openEMS sets the problem up (grid, cells, time step)
% and stops before the first time step, so the run's size is known before hours are committed to it.
% REFERENCE-GENERATION MATERIAL: never on the path of a reference run.
global CRF_OPENEMS
savePath = pwd;
cd(Sim_Path);
system(['"' CRF_OPENEMS '" "' Sim_File '" --no-simulation']);
cd(savePath);
error('crf:setup_only', 'setup only: stopped before the simulation');
end
