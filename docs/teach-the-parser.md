# Teach the parser

Sites add their own words to COBOL: a preprocessor include such as `++INCLUDE`, or a
site statement that a precompiler expands. Starforge does not guess what those words
mean. A program that holds one is marked **Partial**, and the place is listed.
`./doctor.sh teach-parser` lets you teach Starforge such a word with your own AI
endpoint. It runs automatically once you have agreed for an estate, and saves a rule only
when the rule passes its check. `--review` lets you decide every step yourself. Rules stay
on your machine; sharing anything with the Starforge team is your choice.

Teaching only works with Starforge (`PARSER_ENGINE=starforge`).

## Commands

```bash
./doctor.sh teach-parser report      # list what Starforge cannot read, grouped; sends nothing
./doctor.sh teach-parser             # same as "run": teach automatically (asks once per estate)
./doctor.sh teach-parser run --review  # decide group by group what is sent and saved
./doctor.sh teach-parser run --agree   # agree without a prompt (for scripts); remembered
./doctor.sh teach-parser list        # show the taught rules and notes
./doctor.sh teach-parser remove <id> # remove a rule; the next parse reads without it
./doctor.sh teach-parser share <id>  # write a note for the Starforge team; sends nothing
./doctor.sh teach-parser share <id> --github  # show the note, confirm, post it as an issue
./doctor.sh teach-parser forget      # withdraw your agreement; the next run asks again
```

After `rekt-parse` or `rekt-full` with Starforge, doctor lists the places it could not
read and then teaches as `STARFORGE_TEACH` says (shell or `Config/ai-config.local.env`):

| `STARFORGE_TEACH` | After a Starforge parse |
|---|---|
| `auto` (default) | Teaches automatically. Without your agreement for the estate it asks once; with no terminal it stops and sends nothing. If a rule is saved, it parses again. |
| `review` | Offers to teach group by group, then asks before parsing again. Needs a terminal. |
| `off` | Only lists the places and prints the command. |

## Agreeing once per estate

The first automatic run for an estate shows one excerpt exactly as it would be sent and
asks: `[y]es` sends now and in later runs, `[r]eview` switches to group-by-group for this
run, `[n]o` sends nothing. Your answer is kept for those estate folders and that endpoint
in `Config/starforge-teach-consent.local.json`, which git ignores. A new estate folder or a
different endpoint asks again. `forget` deletes the file.

## What happens in `run`

In automatic mode each group is sent without a question, the rule is checked over the
whole estate, and it is saved only when the check passes (steps 4 and 6 below happen on
their own). Site statements are kept as *effects unknown*. The terminal prints one line per
step; the full text is in the run record (see the portal section). `remove <id>` takes a
rule out again.

With `--review`, for each group of places with the same problem and word:

1. Doctor shows the exact text it would send and the endpoint and model it would go to.
   Names chosen by your site are replaced by `W1`, `W2`, …; comments and the contents
   of literals are removed. It is still your code's structure, so your endpoint's own
   data terms apply. The endpoint is the one set up by `./doctor.sh setup`.
2. You answer `y` to send, `s` to skip, or `q` to stop. Nothing is sent without `y`.
3. Your AI sorts the place into one of five categories:

   | Category | Meaning | Result |
   |---|---|---|
   | `dialect-rule` | A site or vendor word | A rule is proposed |
   | `source-error` | The code is wrong and would not compile | No rule; fix the source |
   | `not-cobol` | The file is not a COBOL program | Proposed only when the whole file fails from its first line |
   | `missing-copybook` | A copybook that was not found | No rule; add the copybook |
   | `parser-gap` | Valid standard COBOL that Starforge should read | No rule; a note for the Starforge team is written |

4. Starforge builds the rule from what it found itself (the word and the files); the
   AI only chooses the kind and where a statement ends. Sometimes the problem shows up
   just after the site word, for example when a `MOVE … TO X` without a period runs
   into it. Then the AI may point at the word by its placeholder (`W2`). Starforge maps
   it back to the real word on your machine, and only to a name from that excerpt.
   Starforge then reads the whole estate again with the rule. A rule is offered only
   when it fixes **every** place in the group and changes nothing else.
5. For a site statement, you are asked whether it can change data or decide what runs
   next. Answer `n` only if you are sure it does neither. Otherwise it is kept as
   *effects unknown*, and programs that use it stay Partial in the dependency evidence.
6. You choose whether to save it (`y`; the default is no).

## See a run in the portal

Each `run` is recorded in `output/teach-parser/<run>/teach.json` and shows up in the
portal's **🔁 AI Loop** tab as **Teach parser**. For each group you see the problem and
its first place, the exact text that was sent (or would have been, if you skipped it),
what your AI said, the proposed rule, the check over the whole estate and what was decided.
The header shows the endpoint and model, whether the run was automatic or reviewed by you,
and how many places were unread before and after. A group that left a note shows its id
and the `share` command.
The record is updated after every step, so you can follow a run while it is going. It
holds the same text the terminal showed and stays on your machine under `output/`, like
every other scan result. A record that cannot be written never stops the run.

## Rule kinds

| Kind | Example | What Starforge does |
|---|---|---|
| `copyAlias` | `++INCLUDE MEMBER` | Includes the member the way `COPY` does |
| `statement` | `XYZCALL … END-XYZCALL` or ending at the period | Reads it as one site statement, with effects `none` or `unknown` |
| `notProgram` | Data or definition files in the program folders | Skips those files; they are not counted as programs |

## Where rules are kept

Rules are saved in `Config/starforge-dialect.local.json`, which git ignores. Set
`STARFORGE_RULES` to use another file. Starforge reads the file on every parse when it
exists, and records its SHA-256 (`rulesSha256`) in the parse output, so results show
which rules they were made with. Taught rules are never pushed anywhere.

## Sharing with the Starforge team

Sharing is your choice, and nothing is ever sent on its own. A rule or a parser gap can
help other sites only if the Starforge team learns about it, so `share <id>` writes a note
to `output/teach-parser/notes/<id>.md`:

- **Rule notes** hold only the generic rule (kind, word, where a statement ends) and a
  made-up example. No reason, file names or paths.
- **Parser-gap notes** are written during `run` when your AI says Starforge should read the
  code. They hold the problem and the excerpt that was sent, with every name from your
  estate replaced by `NAME1`, `NAME2`, … and literals shown as `'...'`.
- **`notProgram` rules are never shared**: they only list your own files.

Before a note is written, it is checked against your estate: folder names, file names,
program ids and data names, plus the terms in `Config/sensitive-terms.local.txt` (one per
line, git-ignored). Common words such as `cobol`, `copy` or `src` are not counted. If the
rule word itself is also a name in your estate, the note uses `SITEWORD` instead and says
so; tell the team the real word only if it is not confidential. If anything else matches,
nothing is written. Read the note, then paste it into a GitHub issue yourself, or use
`--github`: it shows the note, asks `y/N`, and posts it with `gh issue create` to
Azure-Samples/Legacy-Modernization-Agents. The issue is public.
