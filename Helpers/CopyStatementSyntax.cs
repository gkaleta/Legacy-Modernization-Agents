namespace CobolToQuarkusMigration.Helpers;

public static class CopyStatementSyntax
{
    // CA IDMS "COPY IDMS [RECORD|FILE|MODULE|LR|MAP-CONTROL|MAP] member". Placed right after COPY\s+, it makes a
    // scanner capture the member instead of "IDMS". "COPY IDMS." and "COPY IDMS OF lib" still name a copybook IDMS.
    public const string IdmsPrefix =
        @"(?i:IDMS\s+(?:(?:RECORD|FILE|MODULE|LR|MAP-CONTROL|MAP)\s+(?=['""]?[A-Z0-9]))?"
        + @"(?!(?:OF|IN|SUPPRESS|REPLACING)(?![A-Z0-9_-]))(?=['""]?[A-Z0-9#@$]))?";
}
