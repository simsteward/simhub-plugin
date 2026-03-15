# Grafana dashboard validation (e.g. last 7 days)

Use these steps to confirm which SimSteward events appear in your Loki data and that dashboards use the correct datasource. Run in **Grafana → Explore → Loki** with time range **Last 7 days**.

## Step 1: Event distribution (count by event)

See which event types exist and their relative volume:

1. **Instant query (table of event counts)**  
   In Explore, switch to **Code** mode and run:
   ```logql
   sum by (event) (count_over_time({app="sim-steward"} | json [$__range]))
   ```
   Use time range **Last 7 days**. If your Loki version supports extracting `event` from the JSON body for grouping, you will get one row per event type with total count. If this returns no grouping (or errors), use the alternative below.

2. **Alternative: Logs + UI grouping**  
   - Query: `{app="sim-steward"} | json`
   - Time range: **Last 7 days**
   - In the log result, use the parsed **Event** derived field (from the Loki datasource config) and Grafana’s “Group by” / filter by Event to see which events exist and approximate volume.

## Step 2: Label check

- Query: `{app="sim-steward"}`
- Confirm in the result that labels include: `env`, `component`, `level`.
- If you use **Grafana Cloud**, note your Loki datasource **UID** (e.g. in Data sources). Provisioned dashboards use UID `loki_local`; either add a Loki datasource with that UID pointing at Cloud, or use the **SimSteward Event Coverage** dashboard (which has a datasource variable `DS_LOKI`) and select your Cloud Loki there.

## Step 3: Component breakdown (optional)

- Query:
  ```logql
  sum by (component) (count_over_time({app="sim-steward"} [$__range]))
  ```
- Time range: **Last 7 days**
- You should see series for `simhub-plugin`, `bridge`, `tracker` (and optionally `dashboard`).

## Outcome

- You’ll see which events have data and whether any important events lack a dedicated dashboard panel.
- After fixing the datasource (local vs Cloud UID), open the SimSteward dashboards listed in **docs/GRAFANA-LOGGING.md** (§ Provisioned dashboards) and confirm they show data where expected.
