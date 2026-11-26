# Teach the parser

Sites add their own words to COBOL: a preprocessor include such as `++INCLUDE`, or a
site statement that a precompiler expands. Starforge does not guess what those words
mean. A program that holds one is marked **Partial**, and the place is listed.
`./doctor.sh teach-parser` lets you teach Starforge such a word with your own AI
endpoint. You approve every step, and the rule stays on your machine.

Teaching only works with Starforge (`PARSER_ENGINE=starforge`).

## Commands

```bash
./doctor.sh teach-parser report      # list what Starforge cannot read, grouped; sends nothing
./doctor.sh teach-parser             # same as "run": walk through the groups with your AI endpoint
./doctor.sh teach-parser list        # show the taught rules
./doctor.sh teach-parser remove <id> # remove a rule; the next parse reads without it
./doctor.sh teach-parser share <id>  # print a generic copy of a rule to send to the Starforge team yourself
```

After `rekt-parse` or `rekt-full` with Starforge, doctor lists the places it could not
read and offers to start teaching. If you save a rule, it offers to parse again.

## What happens in `run`

For each group of places with the same problem and word:

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
   | `parser-gap` | Valid standard COBOL that Starforge should read | No rule; report it to the Starforge team |

4. Starforge builds the rule from what it found itself (the word and the files); the
   AI only chooses the kind and where a statement ends. It then reads the whole estate
   again with the rule. A rule is offered only when it fixes **every** place in the
   group and changes nothing else.
5. For a site statement, you are asked whether it can change data or decide what runs
   next. Answer `n` only if you are sure it does neither. Otherwise it is kept as
   *effects unknown*, and programs that use it stay Partial in the dependency evidence.
6. You choose whether to save it (`y`; the default is no).

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
which rules they were made with. Taught rules are never pushed anywhere. `share` prints
only the generic rule and a made-up example; you open the issue yourself.
