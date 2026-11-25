# CA IDMS programs

Some COBOL programs are written for the CA IDMS precompiler. They contain `COPY IDMS` statements, IDMS sections and IDMS DML statements (`OBTAIN`, `STORE`, `TRANSFER CONTROL`, `DC RETURN`, …) that a plain COBOL parser does not know. This page explains what Starforge reads in those programs, what you must provide, and how REKT is called for them.

Starforge's IDMS support is written from Broadcom's public CA IDMS 19.0 documentation (see [Sources](#sources)). No vendor copybook or record layout is included in this repository.

## What Starforge reads

A program is treated as an IDMS program when it contains a `COPY IDMS` statement. Programs without one parse exactly as before, so words like `OBTAIN`, `FINISH` or `GET` stay ordinary data names there.

| Part | What Starforge does |
| --- | --- |
| `COPY IDMS member` (also `RECORD`, `FILE`, `MODULE`, `LR`, `MAP`, `MAP-CONTROL` forms, `VERSION`, `REDEFINES`) | Looks the member up like any copybook and expands it. With a level number (`01 COPY IDMS EMPLOYEE.`), a copybook that holds only the record's fields gets its record entry (`01 EMPLOYEE.`) rebuilt from the statement. The inclusion is marked `"idms": true` in the parse report. |
| `IDMS-CONTROL SECTION` (Environment Division) | Reads `PROTOCOL. MODE IS … [DEBUG]`, `SUBSCHEMA-NAMES LENGTH IS …` and `IDMS-RECORDS WITHIN|MANUAL`. |
| `SCHEMA SECTION` (Data Division) | Reads `DB subschema WITHIN schema [VERSION n]`. |
| `MAP SECTION` (Data Division) | Reads `MAX FIELD LIST`, and each `MAP name [VERSION n] [TYPE IS …]`. |
| DML statements | Each one becomes an IDMS statement node with its verb phrase (for example `OBTAIN` or `BIND RUN-UNIT`). A statement ends at a period, a semicolon or the start of the next statement, so several statements on one line separated by `;` are read one by one. `STARTPAGE`, `ENDPAGE` and `SNAP` are statements only at the start of a source line. Where a DML verb clashes with a COBOL verb (`ACCEPT`, `RETURN`, `READ`, `WRITE`, `DELETE`, `SET`, `FREE`, `END`), Starforge decides from the words that follow. |
| `ON condition imperative-statement` | Kept as the statement's ON clause, with its statements parsed as normal COBOL. |
| DML `IF` | `IF set MEMBER` and `IF set [IS] [NOT] EMPTY` become set conditions. |

## What shows up in the output

- **Flow (`flow_ast`)**: `BIND RUN-UNIT` and `FINISH` appear as transaction start and end, other DML as dialect nodes. `TRANSFER CONTROL` has a `TRANSFER` child with its target. An ON clause is a branch. `DC RETURN`, and a `TRANSFER … NORETURN|XCTL` without an ON clause, end the path, so code after them is not reached by fall-through.
- **Dependencies (`*-deps.json`)**: `TRANSFER CONTROL TO program` is a program dependency of kind `IDMS_TRANSFER` (for `RETURN`/`LINK` and `NORETURN`/`XCTL`). A target held in a data item is resolved when one constant value reaches it. Every `TRANSFER` and `DC RETURN` is also listed in `dependencyControlTransfers` with kind `IDMS_DC`, the mode (`link-and-return`, `transfer-no-return`, `return-to-idms-dc`) and, for `DC RETURN NEXT TASK CODE`, the next task code.
- **Native export** (`PARSER_ENGINE=starforge`): a literal `TRANSFER` target that exists in the estate is linked as `IDMS_TRANSFER`. A target held in a data item is reported as `dynamic-transfer-unsupported:<name>` instead of being guessed, as for a dynamic `CALL`.

## What you must provide

IDMS programs copy records and control blocks that the IDMS data dictionary generates, such as `SUBSCHEMA-CONTROL`, `IDMS-STATUS`, `MAP-CONTROLS` and each database record. They are not part of the program source. Export them from your dictionary and put them in the estate next to your other copybooks. Starforge does not make them up: a missing one is reported as a missing copybook (`copy-unresolved:<member>` in the native export), and names it would define, such as `DB-END-OF-SET`, show as unresolved.

## REKT and IDMS

`doctor.sh` now passes `--dialect=IDMS` to REKT for programs that contain `COPY IDMS` or an `IDMS-CONTROL SECTION`. Before, REKT read every program as plain COBOL, so IDMS DML was never parsed. Other programs are unchanged.

In our tests with REKT's IDMS dialect, REKT:

- drops all output for a program when one DML statement fails to parse;
- rejects semicolons between DML statements, and fails on several DML statements on one line;
- does not read `LOAD TABLE` or `GET TIME`;
- lets `IF set MEMBER CONNECT … .` take the statements after the period into the `IF`.

Starforge handles all of these.

## Known limits

- When a copybook already has its own record entry, Starforge does not shift its level numbers to the level written in `COPY IDMS`.
- DML operands are not data references. Reaching-definition analysis therefore treats every DML statement as possibly writing any storage, which keeps it safe but less precise.
- Only COBOL DML is supported, not the PL/I or Assembler forms.

## Sources

Broadcom CA IDMS 19.0, *DML Reference for COBOL* (techdocs.broadcom.com/us/en/ca-mainframe-software/database-management/ca-idms-reference/19-0/dml-reference-for-cobol/…):

- [Precompiler directive statements](https://techdocs.broadcom.com/us/en/ca-mainframe-software/database-management/ca-idms-reference/19-0/dml-reference-for-cobol/precompiler-directive-statements.html) (`COPY IDMS`, IDMS-CONTROL, SCHEMA and MAP sections)
- [Precompiler options](https://techdocs.broadcom.com/us/en/ca-mainframe-software/database-management/ca-idms-reference/19-0/dml-reference-for-cobol/precompiler-options.html)
- [COBOL DML statements](https://techdocs.broadcom.com/us/en/ca-mainframe-software/database-management/ca-idms-reference/19-0/dml-reference-for-cobol/cobol-data-manipulation-language-dml-statements.html), including [TRANSFER CONTROL](https://techdocs.broadcom.com/us/en/ca-mainframe-software/database-management/ca-idms-reference/19-0/dml-reference-for-cobol/cobol-data-manipulation-language-dml-statements/transfer-control-cobol.html), [DC RETURN](https://techdocs.broadcom.com/us/en/ca-mainframe-software/database-management/ca-idms-reference/19-0/dml-reference-for-cobol/cobol-data-manipulation-language-dml-statements/dc-return-cobol.html) and [IF](https://techdocs.broadcom.com/us/en/ca-mainframe-software/database-management/ca-idms-reference/19-0/dml-reference-for-cobol/cobol-data-manipulation-language-dml-statements/if-cobol.html)
- [Communications blocks and error detection](https://techdocs.broadcom.com/us/en/ca-mainframe-software/database-management/ca-idms-reference/19-0/dml-reference-for-cobol/communications-blocks-and-error-detection-cobol.html) (`ON` clause and condition names)
