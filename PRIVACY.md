# Privacy Policy

FluentFMPEG does not collect, store, or transmit any personal information,
usage data, or telemetry. All media processing happens locally on your
machine via FFmpeg; input files, output files, and command output never
leave your device.

## FFmpeg download

On first launch, if `ffmpeg`/`ffprobe` are not already on your `PATH`,
FluentFMPEG downloads a static FFmpeg build from
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) on GitHub.
The download races a small `Range` probe across `github.com` and several
public GitHub proxy mirrors (`ghfast.top`, `github.moeyy.xyz`,
`gh-proxy.com`, `ghps.cc`, `mirror.ghproxy.com`); the first responsive host
serves the file.

Because these requests go directly from your machine to GitHub or to the
selected mirror, the privacy policy of whichever host serves the download
applies to that request:

- GitHub: <https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement>
- Each proxy mirror has its own operator and policy; consult the
  corresponding site if you have concerns.

FluentFMPEG itself does not see, log, or relay any of this traffic beyond
what is needed to fetch the FFmpeg archive and write it to
`%LOCALAPPDATA%\FluentFMPEG\ffmpeg\bin\`.

## Contact

Questions or concerns: <https://github.com/l5z12/FluentFMPEG/issues>.
