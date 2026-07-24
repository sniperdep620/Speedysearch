#!/usr/bin/env python3
"""Train Speedysearch's Stage 2 LightGBM model from local clickstream data."""

import argparse
import json
import sys
from pathlib import Path

import lightgbm as lgb
import pandas as pd


FEATURE_NAMES = [
    "is_exact_prefix", "is_trigram_match", "is_levenshtein_match",
    "frecency", "recency_hours", "access_count", "edit_distance",
    "time_hour_bucket", "day_of_week", "active_app_match",
    "same_directory", "file_type_popularity",
]


def load_clickstream(path):
    """Load valid JSON objects from a JSONL clickstream file."""
    rows = []
    with open(path, "r", encoding="utf-8") as stream:
        for line_number, line in enumerate(stream, 1):
            if not line.strip():
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError as error:
                print(f"warning: ignoring malformed line {line_number}: {error}", file=sys.stderr)
    return rows


def feature_row(features):
    return [float(features.get(name, 0.0)) for name in FEATURE_NAMES]


def build_training_data(clickstream_path, min_samples=50):
    """Build one positive and up to nine real displayed negatives per click."""
    clicks = load_clickstream(clickstream_path)
    if len(clicks) < min_samples:
        print(f"Need {min_samples} clicks, have {len(clicks)}. Skipping training.")
        return None

    x_rows, labels = [], []
    for click in clicks:
        selected_id = click.get("selected_id")
        for candidate in click.get("candidates", []):
            features = candidate.get("features")
            if not isinstance(features, dict):
                continue
            x_rows.append(feature_row(features))
            labels.append(1 if candidate.get("id") == selected_id else 0)

    if not x_rows:
        print("Click records contain no candidate features; collect new clicks with the current daemon.")
        return None
    if len(set(labels)) < 2:
        print("Training data needs both selected and unselected candidates. Skipping training.")
        return None
    return pd.DataFrame(x_rows, columns=FEATURE_NAMES), pd.Series(labels, dtype="int8")


def train_and_save(clickstream_path, output_path, min_samples=50):
    result = build_training_data(clickstream_path, min_samples)
    if result is None:
        return False
    features, labels = result
    dataset = lgb.Dataset(features, label=labels, feature_name=FEATURE_NAMES)
    params = {
        "objective": "binary",
        "metric": "binary_logloss",
        "num_leaves": 31,
        "learning_rate": 0.05,
        "max_depth": 5,
        "min_data_in_leaf": 5,
        "feature_pre_filter": False,
        "verbosity": -1,
        "seed": 42,
        "num_threads": 2,
    }
    booster = lgb.train(params, dataset, num_boost_round=100)
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = output_path.with_suffix(output_path.suffix + ".tmp")
    booster.save_model(str(temporary), num_iteration=-1)
    temporary.replace(output_path)
    print(f"Model trained on {len(features)} candidate examples. Saved to {output_path}")
    return True


def main():
    cache = Path.home() / ".cache" / "speedysearch"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--clickstream", type=Path, default=cache / "clickstream.jsonl")
    parser.add_argument("--output", type=Path, default=cache / "ranker.txt")
    parser.add_argument("--min-samples", type=int, default=50)
    args = parser.parse_args()
    if not args.clickstream.exists():
        print(f"Clickstream not found: {args.clickstream}", file=sys.stderr)
        return 1
    return 0 if train_and_save(args.clickstream, args.output, args.min_samples) else 2


if __name__ == "__main__":
    raise SystemExit(main())
