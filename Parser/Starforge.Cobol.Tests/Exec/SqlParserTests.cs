using Starforge.Cobol.Exec;

namespace Starforge.Cobol.Tests.Exec;

public sealed class SqlParserTests
{
    private static List<(string Name, string? Alias, SqlTableOperation Op)> Tables(SqlStatement s)
        => s.Tables.Select(t => (t.Name, t.Alias, t.Operation)).ToList();

    [Fact]
    public void SelectIntoWithJoinsSchemaQualifiedAndCorrelationNames()
    {
        var s = ExecText.Sql("""
            SELECT A.ID, B.NAME
              INTO :WS-ID, :WS-NAME
              FROM PAY.EMPLOYEE A
              INNER JOIN PAY.DEPT AS B ON A.DEPT = B.DEPT
              LEFT OUTER JOIN LOCATION L ON L.ID = B.LOC
             WHERE A.ID = :WS-KEY
            """);

        s.Status.Should().Be(ExecStatus.Parsed);
        s.StatementKind.Should().Be(SqlStatementKind.SelectInto);
        s.Operation.Should().Be("SELECT INTO");
        Tables(s).Should().Equal(
            ("PAY.EMPLOYEE", "A", SqlTableOperation.Select),
            ("PAY.DEPT", "B", SqlTableOperation.Select),
            ("LOCATION", "L", SqlTableOperation.Select));
        s.Tables[0].Schema.Should().Be("PAY");
        s.Tables[0].Table.Should().Be("EMPLOYEE");
        s.HostVariables.Select(h => (h.Name, h.Role)).Should().Equal(
            ("WS-ID", SqlHostVariableRole.Output),
            ("WS-NAME", SqlHostVariableRole.Output),
            ("WS-KEY", SqlHostVariableRole.Input));
    }

    [Fact]
    public void CommaSeparatedFromListAndSubqueryInWhere()
    {
        var s = ExecText.Sql("""
            SELECT COUNT(*) INTO :CNT
              FROM ORDERS O, CUSTOMER C
             WHERE O.CUST = C.ID
               AND C.REGION IN (SELECT R.ID FROM REGION R WHERE R.ACTIVE = 'Y')
            """);

        Tables(s).Should().Equal(
            ("ORDERS", "O", SqlTableOperation.Select),
            ("CUSTOMER", "C", SqlTableOperation.Select),
            ("REGION", "R", SqlTableOperation.Select));
    }

    [Fact]
    public void FunctionArgumentsWithFromAreNotTables()
    {
        var s = ExecText.Sql("SELECT EXTRACT(YEAR FROM HIRED), TRIM(LEADING '0' FROM CODE) INTO :Y, :C FROM STAFF WHERE ID = :K");

        Tables(s).Should().Equal(("STAFF", null, SqlTableOperation.Select));
    }

    [Fact]
    public void CommonTableExpressionNamesAreNotTables()
    {
        var s = ExecText.Sql("""
            DECLARE C1 CURSOR WITH HOLD FOR
              WITH TOTALS (DEPT, AMT) AS
                (SELECT DEPT, SUM(AMT) FROM SALES GROUP BY DEPT)
              SELECT T.DEPT, D.NAME FROM TOTALS T JOIN DEPT D ON D.ID = T.DEPT
            """);

        s.StatementKind.Should().Be(SqlStatementKind.DeclareCursor);
        s.Cursor.Should().Be("C1");
        s.CursorAttributes.Should().Equal("WITH HOLD");
        s.CommonTableExpressions.Should().Equal("TOTALS");
        Tables(s).Should().Equal(("SALES", null, SqlTableOperation.Select), ("DEPT", "D", SqlTableOperation.Select));
    }

    [Theory]
    [InlineData("OPEN C1", SqlStatementKind.Open)]
    [InlineData("CLOSE C1", SqlStatementKind.Close)]
    [InlineData("FETCH NEXT FROM C1 INTO :A", SqlStatementKind.Fetch)]
    [InlineData("FETCH C1 INTO :A", SqlStatementKind.Fetch)]
    public void CursorStatementsNameTheirCursor(string inner, SqlStatementKind kind)
    {
        var s = ExecText.Sql(inner);

        s.StatementKind.Should().Be(kind);
        s.Cursor.Should().Be("C1");
        s.Tables.Should().BeEmpty();
    }

    [Fact]
    public void UpdateAndDeleteWhereCurrentOf()
    {
        var u = ExecText.Sql("UPDATE PAY.EMPLOYEE SET SALARY = :NEW-SAL WHERE CURRENT OF EMP-CUR");
        var d = ExecText.Sql("DELETE FROM EMPLOYEE E WHERE CURRENT OF EMP-CUR");

        Tables(u).Should().Equal(("PAY.EMPLOYEE", null, SqlTableOperation.Update));
        u.WhereCurrentOf.Should().BeTrue();
        u.Cursor.Should().Be("EMP-CUR");
        Tables(d).Should().Equal(("EMPLOYEE", "E", SqlTableOperation.Delete));
        d.WhereCurrentOf.Should().BeTrue();
        d.Cursor.Should().Be("EMP-CUR");
    }

    [Fact]
    public void InsertFromSelectSeparatesTargetAndSourceOperations()
    {
        var s = ExecText.Sql("INSERT INTO ARCHIVE (ID, AMT) SELECT ID, AMT FROM LEDGER WHERE POSTED < :CUTOFF");

        Tables(s).Should().Equal(("ARCHIVE", null, SqlTableOperation.Insert), ("LEDGER", null, SqlTableOperation.Select));
    }

    [Fact]
    public void MergeIntoUsingTable()
    {
        var s = ExecText.Sql("""
            MERGE INTO STOCK S USING DELIVERIES D ON S.ITEM = D.ITEM
              WHEN MATCHED THEN UPDATE SET S.QTY = S.QTY + D.QTY
              WHEN NOT MATCHED THEN INSERT (ITEM, QTY) VALUES (D.ITEM, D.QTY)
            """);

        s.StatementKind.Should().Be(SqlStatementKind.Merge);
        Tables(s).Should().Equal(("STOCK", "S", SqlTableOperation.Merge), ("DELIVERIES", "D", SqlTableOperation.Select));
    }

    [Fact]
    public void IndicatorAndQualifiedHostVariables()
    {
        var s = ExecText.Sql("""
            SELECT NAME, PHONE, CITY
              INTO :CUST-REC.NAME, :WS-PHONE:WS-PHONE-IND, :WS-CITY INDICATOR :WS-CITY-IND
              FROM CUSTOMER WHERE ID = :CUST-REC.ID
            """);

        s.HostVariables.Should().Equal(
            new SqlHostVariable("CUST-REC.NAME", "CUST-REC", "NAME", null, SqlHostVariableRole.Output),
            new SqlHostVariable("WS-PHONE", null, "WS-PHONE", "WS-PHONE-IND", SqlHostVariableRole.Output),
            new SqlHostVariable("WS-CITY", null, "WS-CITY", "WS-CITY-IND", SqlHostVariableRole.Output),
            new SqlHostVariable("CUST-REC.ID", "CUST-REC", "ID", null, SqlHostVariableRole.Input));
    }

    [Fact]
    public void CommentsAndStringLiteralsDoNotYieldTables()
    {
        var s = ExecText.Sql("""
            SELECT NOTE INTO :N -- FROM AUDIT_TRAIL
              FROM MEMO
             WHERE TEXT = 'SELECT X FROM SECRET' /* JOIN HIDDEN */
            """);

        s.Status.Should().Be(ExecStatus.Parsed);
        Tables(s).Should().Equal(("MEMO", null, SqlTableOperation.Select));
        s.Comments.Should().HaveCount(2);
    }

    [Fact]
    public void DeclareTableIsADeclarationNotAnAccess()
    {
        var s = ExecText.Sql("DECLARE PAY.EMPLOYEE TABLE (ID CHAR(6) NOT NULL, SALARY DECIMAL(9, 2))");

        s.StatementKind.Should().Be(SqlStatementKind.DeclareTable);
        Tables(s).Should().Equal(("PAY.EMPLOYEE", null, SqlTableOperation.Declare));
    }

    [Theory]
    [InlineData("COMMIT", SqlStatementKind.Commit)]
    [InlineData("ROLLBACK WORK", SqlStatementKind.Rollback)]
    [InlineData("WHENEVER SQLERROR GO TO ERR-PARA", SqlStatementKind.Whenever)]
    [InlineData("CALL PROC1 (:A, :B)", SqlStatementKind.Call)]
    [InlineData("SET :TS = CURRENT TIMESTAMP", SqlStatementKind.Set)]
    [InlineData("VALUES (CURRENT DATE) INTO :D", SqlStatementKind.ValuesInto)]
    [InlineData("PREPARE STMT1 FROM :SQL-TEXT", SqlStatementKind.Prepare)]
    [InlineData("EXECUTE IMMEDIATE :SQL-TEXT", SqlStatementKind.ExecuteImmediate)]
    [InlineData("EXECUTE STMT1 USING :A", SqlStatementKind.Execute)]
    [InlineData("DESCRIBE STMT1 INTO :SQLDA", SqlStatementKind.Describe)]
    [InlineData("CONNECT TO :LOC", SqlStatementKind.Connect)]
    [InlineData("BEGIN DECLARE SECTION", SqlStatementKind.BeginDeclareSection)]
    [InlineData("END DECLARE SECTION", SqlStatementKind.EndDeclareSection)]
    public void StatementKindsAreClassified(string inner, SqlStatementKind kind)
    {
        var s = ExecText.Sql(inner);

        s.Status.Should().Be(ExecStatus.Parsed);
        s.StatementKind.Should().Be(kind);
        s.Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("DECLARE C2 SENSITIVE STATIC SCROLL CURSOR WITH HOLD WITH RETURN TO CALLER FOR SELECT A FROM T", "SENSITIVE STATIC SCROLL|WITH HOLD|WITH RETURN TO CALLER")]
    [InlineData("DECLARE C2 NO SCROLL CURSOR WITHOUT HOLD FOR SELECT A FROM T", "NO SCROLL|WITHOUT HOLD")]
    public void CursorAttributesAreGroupedIntoPhrases(string inner, string phrases)
    {
        ExecText.Sql(inner).CursorAttributes.Should().Equal(phrases.Split('|'));
    }

    [Fact]
    public void PrepareRecordsTheStatementName()
    {
        ExecText.Sql("PREPARE STMT1 FROM :SQL-TEXT").StatementName.Should().Be("STMT1");
    }

    [Fact]
    public void LockTableAndDeclareGlobalTemporaryTableNameTheirTarget()
    {
        Tables(ExecText.Sql("LOCK TABLE PAY.EMPLOYEE IN EXCLUSIVE MODE")).Should().Equal(("PAY.EMPLOYEE", null, SqlTableOperation.Lock));
        Tables(ExecText.Sql("DECLARE GLOBAL TEMPORARY TABLE SESSION.WORK (ID INT)")).Should().Equal(("SESSION.WORK", null, SqlTableOperation.Declare));
    }

    [Fact]
    public void UnknownStatementIsUnsupported()
    {
        var s = ExecParser.Parse("EXEC SQL FROB THE TABLE END-EXEC", Lexing.ExecKind.Sql, ExecText.Range);

        s.Status.Should().Be(ExecStatus.Unsupported);
        s.Codes().Should().Equal("sql-unknown-statement:FROB");
    }
}
