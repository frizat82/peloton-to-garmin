![Peloton To Garmin Banner](/images/logo/readme_banner.png?raw=true "Peloton to Garmin Banner")
[![GitHub license](https://img.shields.io/github/license/philosowaffle/peloton-to-garmin.svg)](https://github.com/philosowaffle/peloton-to-garmin/blob/master/LICENSE)
[![GitHub Release](https://img.shields.io/github/release/philosowaffle/peloton-to-garmin.svg?style=flat)]()
[![Github all releases](https://img.shields.io/github/downloads/philosowaffle/peloton-to-garmin/total.svg)](https://GitHub.com/philosowaffle/peloton-to-garmin/releases/)
# peloton-to-garmin
[![](https://img.shields.io/static/v1?label=Sponsor&message=%E2%9D%A4&logo=GitHub&color=%23fe8e86)](https://github.com/sponsors/philosowaffle)
<span class="badge-buymeacoffee"><a href="https://www.buymeacoffee.com/philosowaffle" title="Donate to this project using Buy Me A Coffee"><img src="https://img.shields.io/badge/buy%20me%20a%20coffee-donate-yellow.svg" alt="Buy Me A Coffee donate button" /></a></span>

**Peloton Tag:** _#PelotonToGarmin_

Sync your Peloton workouts to Garmin.

* Fetch latest workouts from Peloton
  * Bike, Tread, Rower, Meditation, Strength, Outdoor, and more
* Automatically upload your workout to Garmin
* Convert Peloton workouts to a variety of formats for offline backup
* Earn Badges and credit for Garmin Challenges
* Counts towards VO2 Max and Training Stress Scores
* Supports Garmin accounts protected by Two Step Verification
* Supports mapping Exercises from Strength workouts
* **Garmin Activity Enrichment** — automatically update Garmin activity names, descriptions, and fields with Peloton class info after sync
* **Garmin FIT Merge** — inject Peloton-only metrics (power, cadence, resistance, speed) directly into your Garmin watch-recorded FIT file, keeping your watch data authoritative while adding Peloton detail
* **Training Plan** — view your current fitness (CTL), fatigue (ATL), and form (TSB) scores calculated from your Peloton history, with daily workout intensity recommendations and suggested classes

Head on over to the [Wiki](https://philosowaffle.github.io/peloton-to-garmin) to get started!

![Example Cycling Workout](/images/example_cycle.png?raw=true "Example Cycling Workout")

## Quick Start with Docker Compose

The recommended way to run P2G is with the WebUI via Docker Compose. This gives you a browser-based UI to configure settings, trigger syncs, and view your training plan.

**1. Create a `p2g` group and add your user (Linux/macOS):**

```bash
sudo groupadd -g 1015 p2g
sudo usermod -aG p2g $USER
# Log out and back in for the group change to take effect
```

**2. Create the directory structure:**

```bash
mkdir -p p2g/config/api p2g/config/webui p2g/data p2g/output
cd p2g
```

**3. Create `docker-compose.yaml`:**

```yaml
services:
  p2g-api:
    container_name: p2g-api
    image: philosowaffle/peloton-to-garmin:api-stable
    user: :p2g
    environment:
      - TZ=America/Chicago        # set your local timezone
      - P2G_CONFIG_DIRECTORY=/app/config
    # ports:
    #   - 8001:8080               # optional: expose API directly
    volumes:
      - ./config/api/:/app/config
      - ./data:/app/data          # persists settings across restarts
      - ./output:/app/output      # generated workout files and logs

  p2g-webui:
    container_name: p2g-webui
    image: philosowaffle/peloton-to-garmin:webui-stable
    user: :p2g
    ports:
      - 8002:8080
    environment:
      - TZ=America/Chicago
      - P2G_CONFIG_DIRECTORY=/app/config
    volumes:
      - ./config/webui:/app/config
    depends_on:
      - p2g-api
```

**4. Start P2G:**

```bash
docker compose up -d
```

Then open `http://localhost:8002` in your browser to configure your Peloton and Garmin credentials and start syncing.

> **Note:** The WebUI and GitHub Actions deployment methods store credentials encrypted at rest. See the [Warnings](#warnings) section below for the Console/headless deployment caveat.

## Contributors

Special thanks to all the [contributors](https://github.com/philosowaffle/peloton-to-garmin/graphs/contributors) who have helped improve this project!

If you're interested in contributing to P2G, [start here](https://philosowaffle.github.io/peloton-to-garmin/latest/contributing/).

## Warnings

⚠️ WARNING!!! For the Console or Docker Headless deployments your username and password for Peloton and Garmin Connect are stored in clear text, WHICH IS NOT SECURE. If you have concerns about storing your credentials in an unsecure file, do not use this option.

This warning does not apply to Docker WebUI nor GitHub Actions deployments. Both of these methods store credentials encrypted at rest.

## Donate
<a href="https://www.buymeacoffee.com/philosowaffle" target="_blank"><img src="https://www.buymeacoffee.com/assets/img/custom_images/black_img.png" alt="Buy Me A Coffee" style="height: 41px !important;width: 174px !important;box-shadow: 0px 3px 2px 0px rgba(190, 190, 190, 0.5) !important;-webkit-box-shadow: 0px 3px 2px 0px rgba(190, 190, 190, 0.5) !important;" ></a>
