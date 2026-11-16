# Chapter tags

Each chapter's finished code is an annotated git tag. Readers check out the chapter they are reading:

```bash
git clone https://github.com/DgreatGnazzini/mastering-ai-sharp-code.git
cd mastering-ai-sharp-code
git checkout ch07
```

## Convention

| Tag | Meaning |
|---|---|
| `ch00` | Starting skeleton, before chapter 1 |
| `ch01` … `ch15` | Code as it stands at the end of that chapter |
| `final` | Same commit as `ch15` |
| `ch07-r2` | A post-print fix to a published chapter tag (see below) |

Rules:
- `main` always holds the latest chapter. Tags are cumulative, and each chapter starts from the previous tag, so `git diff ch06 ch07` shows exactly what chapter 7 adds.
- A tag is only created with `scripts/tag-chapter.sh`, which builds and tests first. CI runs again on the tag push.
- Package versions change only between tags, and the change is noted in the GitHub release for that tag.
- Published tags never move. A fix becomes a new tag `chNN-r2` (then `-r3`, …), listed in `ERRATA.md`.

## Chapter READMEs

| Tag | README |
|---|---|
| `ch01` | [docs/chapters/ch01.md](chapters/ch01.md) |
| `ch02` | [docs/chapters/ch02.md](chapters/ch02.md) |
