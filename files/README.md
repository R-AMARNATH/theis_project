# Batch workload pipeline

Three scripts, run in this order, once each (except `batch_job.py`, which runs
once per deployment on the VM):

## 1. Generate the fixed dataset (run once, locally)

```bash
pip install -r requirements.txt
python generate_dataset.py --out-dir ./data --seed 42
```

Produces `data/dataset.csv` (~200MB, 2.7M synthetic trip records) and
`data/zone_lookup.csv`. This is your controlled variable for all 84 cycles —
do not regenerate mid-experiment.

## 2. Stage it into every candidate region (run once, locally)

```bash
cp manifest.sample.json manifest.json   # fill in your real bucket/account/project names
python stage_dataset.py --manifest manifest.json --data-dir ./data
```

Requires cloud credentials locally (`aws configure`, `AZURE_STORAGE_CONNECTION_STRING_<ACCOUNT>`
env vars per Azure account, `gcloud auth application-default login` for GCP).

Azure note: the storage **account** must already exist in the target region —
this script only creates the **container** inside it. Create accounts once via:
```bash
az storage account create --name carbonawestagingweu --location westeurope \
  --resource-group <rg> --sku Standard_LRS
```

## 3. Run the batch job (runs on the VM, once per deployment)

This is what the GitHub Actions workflow calls after the VM boots:

```bash
pip install -r requirements.txt
python batch_job.py \
  --cloud aws --region eu-west-1 --bucket carbonaware-data-aws-eu-west-1 \
  --cycle-id 17 --objective-type multi --weight-config balanced \
  --output-json result.json
```

Prints one JSON line to stdout with `latency_actual_sec`, `execution_time_sec`,
`deployment_success`, and `error_notes`. The workflow captures that line and
merges it with the scheduler's `*_predicted` fields (MOER, cost, latency) —
this script has no access to those, by design, so it stays a pure "run and
report what actually happened" component.

## Not yet done

- The GitHub Actions workflow doesn't call `batch_job.py` or tear the VM down
  yet — `deploy-aws-vm.yml` currently just launches an idle instance.
- No Azure or GCP workflow files exist yet.
- No DB table exists yet to persist the merged result rows (only advice
  requests are logged today, not deployment outcomes).
