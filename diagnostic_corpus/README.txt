Glide automated diagnostic fixtures. Generated for Phase 17A diagnostics and later performance audits.
Files are intentionally tiny except the 90-93 settings/performance fixtures used to prove preview/quality behavior.
Codec-dependent entries are expected to pass only when Windows/WIC has the corresponding decoder.

Glide 2.5 adds 93_large_progressive_portrait_21mp.jpg (3746x5616, ~21 MP, progressive JPEG, 4:2:0)
as a non-user synthetic regression fixture for the large portrait/progressive class that exposed Glide 2.x's
full-raster-before-scale performance regression. Its 10-scan progressive script deliberately starts with separate
Y-DC, Cb-DC and Cr-DC scans, matching the field-image class that can show a monochrome level-0 first frame.
This fixture therefore audits both decoder-native reduced first paint and the colour-first progressive policy.
Future audits should record cold and warm first-paint latency, decode route (shell-cache / wic-native-scale /
stream), chosen progressive policy, output preview size, first-frame colour state, and settled refinement latency.
