Raw CCITT streams written by egami-compare's `tools/make-ccitt-matrix.py`, an encoder written from ITU-T T.4 and T.6, for the
CCITT tests and as seeds for the `ccitt` fuzz target. Each `.ccitt` stream has its PDF DecodeParms in the `.json` beside it;
every `shapes-*` stream decodes to `shapes.pbm` (1 is black). `damaged-*` streams have three bytes overwritten in the middle.
Public domain.
