# Local anatomical models (never committed)

circuitRF ships no anatomical model (brief-sar-11, overview L2). A developer who has obtained a free segmented model
themselves may put it here to exercise the importers on real anatomy:

- a label volume: `<stem>.nii`, `<stem>.nii.gz` or a DiscMaterial `<stem>.h5`;
- optionally its label list beside it, `<stem>.csv` (`label,name[,material]`).

Everything in this folder except this README and its `.gitignore` is ignored by git. Do not commit a model, its name or
its licence text anywhere in the repository, and do not convert one for distribution.

`LocalModelImportTests` reads every model it finds here, prints the import's size report, and skips with a reason when
the folder holds none.
