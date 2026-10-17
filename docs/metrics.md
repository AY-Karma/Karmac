# Observed connection timings

These are measurements observed during local development on 16 October 2026, from one Windows laptop and AirPods Pro 3. They describe the tested software workflow, not a promise for every adapter, driver, Apple model, or radio environment. They have not been freshly reproduced as a controlled comparison benchmark.

![Observed connection timings](assets/connection-metrics.svg)

| Operation | Observed elapsed time | Samples |
| --- | ---: | ---: |
| Bluetooth-off cold-start workflow, run 1 | 15,493 ms | 1 |
| Bluetooth-off cold-start workflow, run 2 | 4,674 ms | 1 |
| Already-connected output verification | 211 ms | 1 |

The two cold-start runs cover enabling Bluetooth, verifying the saved pair, requesting the audio connection until the playback endpoint is active, and switching and verifying Windows output across its three roles. They are separate observations; no average, percentile, or speedup is claimed from this small sample.

The 211 ms result begins with the AirPods already connected. It verifies output and must not be compared as another cold-start result. Bluetooth-off cold start is also distinct from physically waking earbuds after a closed-case sleep.

These measurements do not establish audible first-sound latency, a physical case wake time, or application launch time. No controlled Windows Settings baseline was completed, and no performance claim is made for other earphone models. Apps with a fixed output selection can keep their own device choice.

![Connection sequence](assets/connection-flow.svg)
