# vendor

Third-party binaries that the installer bundles. They are not stored in this repository.

- `models/ggml-base.bin`: the Whisper "base" speech model used for clip transcripts (about 141 MB).
  Download it from https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin and put it here before building the installer.
  If it is missing, Yappest downloads the same file on first use instead.
- `vbcable/VBCABLE_Driver_Pack43.zip`: only for the private "full" installer. The public installer leaves it out and
  Yappest fetches the pack from VB-Audio's own download page when it first sets up Yappest Input.
