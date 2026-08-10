"""
batch_job.py

Runs ON the deployed VM (AWS/Azure/GCP), one execution per deployment in the
experiment. Pulls the fixed dataset from same-region storage, does real
pandas CPU+I/O work, writes a small aggregated output back, and prints a
single JSON result row to stdout matching the experiment logging schema.

The GitHub Actions workflow that launches the VM is responsible for:
  1. Injecting cloud/region/storage identifiers as env vars or CLI args
  2. Capturing this script's output-json file and POSTing it to the API's
     POST /results/actual endpoint, where it's upserted by (cycle_id,
     cloud_provider, region) against the row that /results/predicted
     already wrote for this cycle -- this script has no access to the
     scheduler's *_predicted fields, so it never sets them itself. See the
     "Report actual result to CarbonAware API" step in the workflow yml.
  3. Terminating the VM once the script exits

Usage:
  python batch_job.py --cloud aws --region eu-west-1 --bucket carbonaware-data-aws-eu-west-1 \
      --cycle-id 17 --objective-type multi --weight-config balanced

Exit code 0 on success (even if deployment_success=false is logged for a
soft failure); non-zero only on unrecoverable script errors.
"""
import argparse
import json
import sys
import time
import tempfile
from pathlib import Path
from datetime import datetime, timezone

import numpy as np
import pandas as pd


def download_aws(bucket, region, tmpdir: Path):
    import boto3
    s3 = boto3.client("s3", region_name=region)
    for fname in ["dataset.csv", "zone_lookup.csv"]:
        s3.download_file(bucket, fname, str(tmpdir / fname))


def download_azure(account, container, tmpdir: Path):
    from azure.storage.blob import BlobServiceClient
    import os
    conn_str = os.environ.get(f"AZURE_STORAGE_CONNECTION_STRING_{account.upper()}") \
        or os.environ.get("AZURE_STORAGE_CONNECTION_STRING")
    if not conn_str:
        raise RuntimeError(f"AZURE_STORAGE_CONNECTION_STRING not set for account {account}")
    svc = BlobServiceClient.from_connection_string(conn_str)
    for fname in ["dataset.csv", "zone_lookup.csv"]:
        with open(tmpdir / fname, "wb") as f:
            f.write(svc.get_blob_client(container, fname).download_blob().readall())


def download_gcp(project, bucket, tmpdir: Path):
    from google.cloud import storage
    client = storage.Client(project=project)
    b = client.bucket(bucket)
    for fname in ["dataset.csv", "zone_lookup.csv"]:
        b.blob(fname).download_to_filename(str(tmpdir / fname))


def upload_aws(bucket, region, local_path: Path, key: str):
    import boto3
    boto3.client("s3", region_name=region).upload_file(str(local_path), bucket, key)


def upload_azure(account, container, local_path: Path, key: str):
    from azure.storage.blob import BlobServiceClient
    import os
    conn_str = os.environ.get(f"AZURE_STORAGE_CONNECTION_STRING_{account.upper()}") \
        or os.environ.get("AZURE_STORAGE_CONNECTION_STRING")
    svc = BlobServiceClient.from_connection_string(conn_str)
    with open(local_path, "rb") as f:
        svc.get_blob_client(container, key).upload_blob(f, overwrite=True)


def upload_gcp(project, bucket, local_path: Path, key: str):
    from google.cloud import storage
    client = storage.Client(project=project)
    client.bucket(bucket).blob(key).upload_from_filename(str(local_path))


def run_transform(trips: pd.DataFrame, zones: pd.DataFrame) -> pd.DataFrame:
    """The compute-heavy phase: filter, groupby aggregation, join, rolling window."""
    # 1. Filter out junk/degenerate trips
    trips = trips[(trips["trip_distance"] > 0.1) & (trips["fare_amount"] > 0)]

    # 2. Join against the zone lookup table
    trips = trips.merge(zones, left_on="pickup_zone_id", right_on="zone_id", how="left")

    # 3. Rolling-window computation: 1000-trip rolling average fare, in pickup-time order
    trips = trips.sort_values("pickup_datetime")
    trips["fare_rolling_avg"] = trips["fare_amount"].rolling(window=1000, min_periods=1).mean()

    # 4. Groupby aggregation: per-borough summary stats
    summary = trips.groupby("borough", dropna=False).agg(
        trip_count=("trip_id", "count"),
        avg_fare=("fare_amount", "mean"),
        avg_tip=("tip_amount", "mean"),
        avg_distance=("trip_distance", "mean"),
        avg_fare_rolling=("fare_rolling_avg", "mean"),
    ).reset_index()

    return summary


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cloud", required=True, choices=["aws", "azure", "gcp"])
    ap.add_argument("--region", required=True)
    ap.add_argument("--cycle-id", required=True)
    ap.add_argument("--objective-type", default="multi", choices=["single", "multi"])
    ap.add_argument("--weight-config", default="")

    # storage identifiers -- only the ones relevant to --cloud need to be set
    ap.add_argument("--bucket", help="AWS/GCP bucket name")
    ap.add_argument("--project", help="GCP project id")
    ap.add_argument("--account", help="Azure storage account name")
    ap.add_argument("--container", help="Azure container name")

    ap.add_argument("--output-json", default="result.json",
                     help="Path to write the result row locally (also printed to stdout)")
    args = ap.parse_args()

    timestamp_start = datetime.now(timezone.utc).isoformat()
    t_job_start = time.time()
    deployment_success = True
    error_notes = None
    latency_actual = None
    execution_time = None

    try:
        with tempfile.TemporaryDirectory() as tmpdir_str:
            tmpdir = Path(tmpdir_str)

            # --- Step: pull dataset from same-region storage, measure latency ---
            t0 = time.time()
            if args.cloud == "aws":
                download_aws(args.bucket, args.region, tmpdir)
            elif args.cloud == "azure":
                download_azure(args.account, args.container, tmpdir)
            elif args.cloud == "gcp":
                download_gcp(args.project, args.bucket, tmpdir)
            latency_actual = time.time() - t0

            # --- Step: compute phase ---
            trips = pd.read_csv(tmpdir / "dataset.csv", parse_dates=["pickup_datetime", "dropoff_datetime"])
            zones = pd.read_csv(tmpdir / "zone_lookup.csv")
            summary = run_transform(trips, zones)

            # --- Step: write output back ---
            out_local = tmpdir / "output.csv"
            summary.to_csv(out_local, index=False)
            out_key = f"output/result_{args.cycle_id}_{int(time.time())}.csv"
            if args.cloud == "aws":
                upload_aws(args.bucket, args.region, out_local, out_key)
            elif args.cloud == "azure":
                upload_azure(args.account, args.container, out_local, out_key)
            elif args.cloud == "gcp":
                upload_gcp(args.project, args.bucket, out_local, out_key)

    except Exception as e:
        deployment_success = False
        error_notes = f"{type(e).__name__}: {e}"

    execution_time = time.time() - t_job_start
    timestamp_end = datetime.now(timezone.utc).isoformat()

    result_row = {
        "cycle_id": args.cycle_id,
        "objective_type": args.objective_type,
        "weight_config": args.weight_config or None,
        "cloud_provider": args.cloud,
        "region": args.region,
        "timestamp_start": timestamp_start,
        "timestamp_end": timestamp_end,
        "latency_actual_sec": round(latency_actual, 3) if latency_actual is not None else None,
        "execution_time_sec": round(execution_time, 3),
        "deployment_success": deployment_success,
        "error_notes": error_notes,
    }

    with open(args.output_json, "w") as f:
        json.dump(result_row, f)

    # single JSON line on stdout -- the workflow greps/parses this
    print(json.dumps(result_row))

    if not deployment_success:
        sys.exit(0)  # soft failure is logged, not a script crash


if __name__ == "__main__":
    main()
