# TestData — document corpus (not committed)

The corpus tests in `DocumentCorpusTests.cs` run the real engine adapters over **real
questionnaire documents**. Those documents are confidential client material and are
**deliberately excluded from the repository** (see the root `.gitignore`).

- With no documents present, the corpus tests **no-op and pass** — a fresh clone builds and
  tests green.
- To activate the full regression locally, drop the documents listed in
  `DocumentCorpusTests.Corpus` into this folder (they are copied to the test output by the
  csproj glob).
- To add a new document: copy it here and add a row to `Corpus` with the minimum expected
  data-entry table count.

Never commit a real questionnaire — public repo.
