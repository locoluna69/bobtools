# OCR Performance Tuning Notes — 2026-03-17

## Why this was worth documenting

The signature OCR path improved substantially through iterative tuning rather than one large rewrite.

The useful lesson is that accuracy and performance moved together once the pipeline stopped spending time on obviously bad regions and obviously bad OCR candidates.

## Starting point

The original signature-specific path already had a good architectural idea:

- locate the signature icon
- crop candidate regions to the right of the icon
- OCR those candidate regions with a numeric whitelist
- rank the results

The problem was that the implementation still explored too many candidate regions and too many OCR variants per region.

In the early log entries, that led to cases such as:

- `46` to `80` candidate regions
- hundreds to thousands of candidate OCR attempts
- `signature_candidate_ocr` taking tens or hundreds of seconds in pathological runs

## Instrumentation added first

Before changing the heuristics aggressively, the OCR path was instrumented with append-only JSONL logs under `LocalAppData/BobTools/Logs`.

Important fields added during this work:

- per-request timings
- per-stage timings such as:
  - `signature_icon_locate`
  - `signature_candidate_regions`
  - `signature_candidate_ocr`
  - `total`
- whether the signature fast path was attempted
- whether it was used
- whether fallback OCR was used
- per-region candidate statistics
- rejection reasons for fast-path failures

This was the turning point because the tuning could then be based on measured bottlenecks instead of intuition.

## Major tuning steps

### 1. Engine reuse

Two Tesseract pools were introduced:

- a default engine pool
- a numeric-whitelist engine pool

Why it helped:

- it removed repeated engine construction from the hot path
- it made later stage timings easier to interpret because startup noise dropped

### 2. Candidate-region reduction

The signature value search initially explored too many width/height/offset combinations.

That was narrowed to a smaller set of plausible regions near the icon.

Why it helped:

- fewer candidate crops
- fewer OCR passes
- better odds of early stop on the first valid region

### 3. Reduced per-region OCR attempts

Not every threshold variant was equally useful.

The pipeline was updated to prefer a small, prioritized subset such as:

- `contrast_threshold_otsu_minus20`
- `base_threshold_otsu_minus20`
- `contrast_threshold_otsu_minus15`

Why it helped:

- most good reads came from a narrow family of variants anyway
- runtime dropped without materially reducing healthy-path accuracy

### 4. Early-stop logic

Once a region produced a strong strict numeric candidate with enough support, the pipeline stopped scanning the rest of the region list.

Why it helped:

- healthy signature scans usually do not need exhaustive exploration
- this converted many runs from full sweeps into single-region wins

### 5. Domain-aware ranking

The pipeline stopped treating all numeric-looking OCR outputs as equal.

Ranking was improved by:

- preferring comma-formatted multi-digit values
- preferring values that match the signature catalog semantics
- correcting likely `5`/`6`/`8` confusions against valid signature multiples

Why it helped:

- several failure cases were not "no OCR found"
- they were "clean but wrong number found"

### 6. Fast-path rejection gating

One important correction was to stop calling the signature fast path a success just because it returned something.

The fast path now rejects:

- implausible icon placements
- low-quality icon matches
- weak numeric candidates
- approximate-only results that are not strong enough to trust

Why it helped:

- it prevented one-character junk reads from being treated as successful signature extraction
- it made logs more honest and tuning easier

### 7. Plausibility-aware icon search

The icon search previously kept only the globally best template score.

That could select visually strong but geometrically useless icon matches, especially near the right edge of the image.

The search now prefers icon matches that are also plausible for a signature number to exist to the right.

Why it helped:

- it reduced false icon anchors
- it improved the chance that "clear" images stay on the fast path instead of falling into generic fallback OCR

### 8. Signature-specific fallback scoring

For the final difficult images, the issue was no longer the fast path alone.

Fallback OCR sometimes contained a usable numeric token inside a noisy OCR block, but the general scorer preferred longer garbage text.

The fallback scorer for signature captures was updated to:

- strongly reward parsable signature-like numeric tokens
- reward catalog-like matches and corrections
- penalize letter-heavy junk
- penalize excessive multi-line noise
- normalize signature fallback output to the preferred numeric token when one exists

Why it helped:

- this rescued cases where a fallback attempt already contained something like `8,000`, but the old logic selected a larger nonsense block instead

## Measured drift and current performance picture

Using the current OCR log as a trend source:

- healthy fast-path scans are now roughly stable around `0.7s`
- current `250x116` fast-path median total time is about `704 ms`
- current `206x72` fast-path median total time is about `707 ms`

The remaining expensive cases are mostly fallback-driven outliers:

- `250x116` fallback median is on the order of `6s`
- `488x215` fallback median is on the order of `30s`

Interpretation:

- tuning did not materially slow the healthy path
- most remaining performance pain now comes from the small number of images that still miss the fast path

## Practical lessons learned

### 1. Logging came before good tuning

Without stage timings and per-region counts, it would have been too easy to "improve" the wrong thing.

### 2. Accuracy and speed were linked

When the pipeline stopped exploring bad icon placements and bad candidate regions, both correctness and runtime improved.

### 3. Domain semantics mattered more than generic OCR scoring

Knowing that signature numbers come from a constrained catalog and are usually comma-formatted made the ranking logic much stronger.

### 4. Fallback should not behave like the mining path

Signature fallback needs to prioritize numeric token extraction, not verbose text capture.

## Remaining weak spots

The final stubborn signature images still point to a few likely future improvements:

- secondary icon-search pass constrained to the expected in-game UI band
- build/version tagging inside OCR logs for exact drift tracking
- source image names or scenario IDs inside OCR logs for offline comparison sets
- explicit pipeline outcome categories for easier summarization

## Files most relevant to this work

- `Services/OcrService.cs`
- `Services/OcrPipelineLogger.cs`
- `Utils/SignatureCatalog.cs`
- `ViewModels/MainViewModel.cs`
- `Assets/data/signatures.csv`