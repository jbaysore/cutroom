# Third-party components

Cutroom downloads these pinned components with `scripts/bootstrap.ps1` (tools) or on first run (models). Nothing here is committed to the repository.

| Component | Use | License |
|---|---|---|
| FFmpeg 8.1 (BtbN LGPL shared build) | Decoding, proxies, audio extraction, encoding (AMF), loudness | LGPL-2.1+ |
| whisper.cpp / ggml (release b5454) | Local speech-to-text | MIT |
| Whisper large-v3-turbo and base.en weights (ggml) | Transcription models | MIT |
| DeepFilterNet 0.5.6 (`deep-filter` CLI) | Background noise removal | MIT / Apache-2.0 |
| RNNoise model `sh.rnnn` (GregorR/rnnoise-models) | Fallback noise removal via FFmpeg `arnndn` | BSD-3-Clause |
| Silero VAD (ONNX) | Speech detection safety net | MIT |
| wav2vec2-base-960h (ONNX, Xenova export) | Word-boundary forced alignment | Apache-2.0 |
| ONNX Runtime | Runs the ONNX models (CPU) | MIT |
| Montserrat (Julieta Ulanovsky) | Default subtitle and card fonts | SIL Open Font License 1.1 |
| NAudio | Audio playback and recording | MIT |
| DocumentFormat.OpenXml | .docx script import | MIT |
| Microsoft.Data.Sqlite / SQLite | Library index | MIT / Public domain |
