"""
generate_dataset.py

Generates the fixed 200MB synthetic dataset used across ALL 84 cycles and all
regions/clouds. Run this ONCE. The output files are then staged to every
candidate region's bucket by stage_dataset.py, unchanged, for the entire
experiment. Do not regenerate mid-experiment -- the dataset itself is a
controlled variable.

Produces two files:
  - dataset.csv      (~200MB, ~2.7M synthetic taxi-style trip records)
  - zone_lookup.csv  (small lookup table joined against in the batch job)

Usage:
  python generate_dataset.py --out-dir ./data --seed 42
"""
import argparse
import time
from pathlib import Path

import numpy as np
import pandas as pd

N_ROWS = 2_700_000   # tuned empirically to land at ~200MB CSV output
N_ZONES = 264         # NYC-TLC-style zone count, arbitrary but fixed


def generate_zone_lookup(rng: np.random.Generator) -> pd.DataFrame:
    boroughs = ["Manhattan", "Brooklyn", "Queens", "Bronx", "Staten Island", "EWR"]
    service_types = ["Yellow", "Green", "FHV"]
    return pd.DataFrame({
        "zone_id": np.arange(1, N_ZONES + 1),
        "borough": rng.choice(boroughs, N_ZONES),
        "service_type": rng.choice(service_types, N_ZONES),
        "base_congestion_factor": np.round(rng.uniform(0.8, 1.5, N_ZONES), 3),
    })


def generate_trips(rng: np.random.Generator, n: int) -> pd.DataFrame:
    pickup = pd.Timestamp("2025-01-01") + pd.to_timedelta(
        rng.integers(0, 60 * 24 * 90, n), unit="m"
    )
    df = pd.DataFrame({
        "trip_id": np.arange(1, n + 1),
        "pickup_datetime": pickup,
        "pickup_zone_id": rng.integers(1, N_ZONES + 1, n),
        "dropoff_zone_id": rng.integers(1, N_ZONES + 1, n),
        "passenger_count": rng.integers(1, 6, n),
        "trip_distance": np.round(rng.exponential(3.0, n), 2),
        "fare_amount": np.round(rng.uniform(3, 80, n), 2),
        "tip_amount": np.round(rng.exponential(2.0, n), 2),
        "payment_type_id": rng.integers(1, 4, n),
    })
    df["dropoff_datetime"] = df["pickup_datetime"] + pd.to_timedelta(
        rng.integers(3, 90, n), unit="m"
    )
    return df


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default="./data")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--rows", type=int, default=N_ROWS)
    args = ap.parse_args()

    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    rng = np.random.default_rng(args.seed)

    t0 = time.time()
    trips = generate_trips(rng, args.rows)
    trips_path = out_dir / "dataset.csv"
    trips.to_csv(trips_path, index=False)
    print(f"dataset.csv: {trips_path.stat().st_size / 1_048_576:.1f} MB "
          f"({args.rows:,} rows) in {time.time() - t0:.1f}s")

    zones = generate_zone_lookup(rng)
    zones_path = out_dir / "zone_lookup.csv"
    zones.to_csv(zones_path, index=False)
    print(f"zone_lookup.csv: {zones_path.stat().st_size / 1024:.1f} KB "
          f"({len(zones)} rows)")

    print("\nIMPORTANT: these two files are now your fixed experiment inputs.")
    print("Run stage_dataset.py to copy them, byte-for-byte, into every")
    print("candidate region's bucket. Do not regenerate until the 84-cycle")
    print("run is complete.")


if __name__ == "__main__":
    main()
