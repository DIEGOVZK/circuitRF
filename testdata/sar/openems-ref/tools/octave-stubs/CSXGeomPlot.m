function CSXGeomPlot(CSX_filename, args_string)
% circuitRF SAR-1 -- stands in for openEMS's CSXGeomPlot (the AppCSXCAD geometry viewer) in a headless run.
% REFERENCE-GENERATION MATERIAL: first on the path when run_birdcage.m runs a tutorial; it opens nothing.
if nargin < 2
    args_string = '';
end
printf('[wrapper] geometry viewer skipped: CSXGeomPlot %s %s\n', CSX_filename, args_string);
end
