use criterion::{black_box, criterion_group, criterion_main, Criterion};
use speedysearch::cache::{ClickstreamEntry, ClickstreamLogger};
use speedysearch::index::{
    now_secs, EntryMetadata, EntryType, IndexEntry, IndexSnapshot, SearchIndex, TrigramIndex,
};
use speedysearch::{
    FeatureExtractor, IntentClassifier, QueryContext, QueryIntent, RankerFeatures, RankerModel,
    SearchLauncher, Stage1Filter,
};
use std::collections::HashMap;

fn fixture(size: usize) -> SearchIndex {
    let entries = (0..size)
        .map(|id| {
            let name = if id == size - 1 {
                "document".to_string()
            } else {
                format!("sample_file_{id:06}.txt")
            };
            IndexEntry {
                id: id as u64,
                entry_type: EntryType::File,
                name: name.clone(),
                path: format!("/data/{name}"),
                icon_path: None,
                frecency_score: 0.0,
                last_accessed: now_secs(),
                access_count: 1,
                trigrams: TrigramIndex::generate_trigrams(&name),
                metadata: EntryMetadata::default(),
            }
        })
        .collect();
    SearchIndex::from_snapshot(IndexSnapshot {
        entries,
        frecency: HashMap::new(),
        last_indexed: now_secs(),
    })
}

fn benchmarks(c: &mut Criterion) {
    let index = fixture(100_000);
    let classifier = IntentClassifier::with_known_values(&[], &[], &["txt"]);
    c.bench_function("bench_stage0_classify", |b| {
        b.iter(|| classifier.classify(black_box("document")))
    });
    let filter = Stage1Filter::new(index.clone());
    c.bench_function("bench_stage1_filter", |b| {
        b.iter(|| filter.filter(black_box("document"), QueryIntent::Mixed))
    });
    let launcher = SearchLauncher::new(index).expect("benchmark fixture should be valid");
    c.bench_function("bench_full_query", |b| {
        b.iter(|| launcher.query(black_box("document")))
    });

    let sample = fixture(1)
        .snapshot()
        .expect("fixture snapshot")
        .entries
        .remove(0);
    let extractor = FeatureExtractor::from_entries(std::slice::from_ref(&sample));
    let context = QueryContext {
        timestamp: now_secs(),
        hour_of_day: 12,
        day_of_week: 3,
        ..QueryContext::default()
    };
    c.bench_function("bench_feature_extraction", |b| {
        b.iter(|| {
            extractor.extract(
                black_box(&sample),
                black_box(&context),
                black_box("document"),
            )
        })
    });

    let directory = tempfile::tempdir().expect("temporary model directory");
    let model_path = directory.path().join("ranker.txt");
    std::fs::write(
        &model_path,
        "tree\nfeature_names=is_exact_prefix is_trigram_match is_levenshtein_match frecency recency_hours access_count edit_distance time_hour_bucket day_of_week active_app_match same_directory file_type_popularity\nTree=0\nnum_leaves=2\nsplit_feature=0\nthreshold=0.5\ndecision_type=2\nleft_child=-1\nright_child=-2\nleaf_value=-1 1\n",
    )
    .expect("write benchmark model");
    let model =
        RankerModel::load_from_file(&model_path.to_string_lossy()).expect("load benchmark model");
    let features = vec![RankerFeatures::default(); 10];
    c.bench_function("bench_ml_prediction_10_entries", |b| {
        b.iter(|| {
            for features in black_box(&features) {
                black_box(model.predict(features));
            }
        })
    });

    let click_logger =
        ClickstreamLogger::at_path(directory.path().join("benchmark-clickstream.jsonl"))
            .expect("create benchmark clickstream");
    let click = ClickstreamEntry {
        query: "document".into(),
        selected_id: 1,
        selected_name: "document".into(),
        timestamp: now_secs(),
        context: context.clone(),
        rank_position: 1,
        candidates: Vec::new(),
    };
    c.bench_function("bench_clickstream_write", |b| {
        b.iter(|| click_logger.log(black_box(&click)))
    });

    let cache_index = fixture(10_000);
    let snapshot = cache_index
        .snapshot()
        .expect("benchmark fixture should be readable");
    c.bench_function("bench_index_serialize", |b| {
        b.iter(|| bincode::serialize(black_box(&snapshot)))
    });
    let bytes = bincode::serialize(&snapshot).expect("benchmark fixture should serialize");
    c.bench_function("bench_index_deserialize", |b| {
        b.iter(|| bincode::deserialize::<IndexSnapshot>(black_box(&bytes)))
    });
}

criterion_group!(benches, benchmarks);
criterion_main!(benches);
