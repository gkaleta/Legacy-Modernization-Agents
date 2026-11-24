namespace Starforge.Cobol.Exec;

// One CICS command form. VerbArgument is set when the last verb word itself takes an argument, as in SEND MAP(name).
internal sealed record CicsCommandSpec(
    string[] Words,
    bool OptionalQualifier,
    char VerbArgument,
    IReadOnlyDictionary<string, char> Options,
    bool OpenEnded)
{
    public string Verb => string.Join(' ', Words);
}

// Application programming commands of IBM CICS TS, authored from the public command reference. Line format:
//   "WORD [WORD...] : OPTION() OPTION OPTION(?)"  ()=argument required, (?)=optional, none=no argument.
// A leading '~' marks an open-ended command (SPI and condition lists) whose option names are not validated;
// a qualifier written as [TS] may be omitted in source and is then assumed.
internal static class CicsCommandTable
{
    public const char NoArgument = 'n';
    public const char Required = 'r';
    public const char Optional = 'o';

    // Valid on every command: the translator accepts them regardless of the verb.
    public static readonly IReadOnlyDictionary<string, char> Common = new Dictionary<string, char>(StringComparer.Ordinal)
    {
        ["RESP"] = Required,
        ["RESP2"] = Required,
        ["NOHANDLE"] = NoArgument,
        ["SYSEIB"] = NoArgument,
    };

    private const string Containers = "CHANNEL() ACTIVITY() ACQACTIVITY PROCESS ACQPROCESS";
    private const string FileKeys = "FILE() DATASET() RIDFLD() KEYLENGTH() GENERIC SYSID() RBA RRN XRBA";
    private const string Paging = "OUTPARTN() ACTPARTN() LDC() ACCUM TERMINAL SET() PAGING REQID() WAIT LAST HONEOM L40 L64 L80 FORMFEED NLEOM PRINT FREEKB ALARM ERASE ERASEAUP DEFAULT ALTERNATE MSR() FMHPARM() NOFLUSH";
    private const string TimeIntervals = "INTERVAL() TIME() AFTER AT HOURS() MINUTES() SECONDS() MILLISECS()";
    private const string Appc = "CONVID() SESSION() STATE() ATTACHID()";

    private static readonly string[] SpiResources =
    {
        "ASSOCIATION", "ATOMSERVICE", "AUTINSTMODEL", "AUTOINSTALL", "BRFACILITY", "BUNDLE", "BUNDLEPART", "CAPTURESPEC",
        "CFDTPOOL", "CLASSCACHE", "CONNECTION", "CORBASERVER", "DB2CONN", "DB2ENTRY", "DB2TRAN", "DELETSHIPPED", "DISPATCHER",
        "DOCTEMPLATE", "DSNAME", "DUMPDS", "ENQ", "ENQMODEL", "EPADAPTER", "EPADAPTERSET", "EVENTBINDING", "EVENTPROCESS",
        "EXCI", "EXITPROGRAM", "FEPI", "FILE", "HOST", "IPCONN", "IRC", "JOURNALMODEL", "JOURNALNAME", "JVM", "JVMPOOL",
        "JVMPROFILE", "JVMSERVER", "LIBRARY", "MODENAME", "MONITOR", "MQCONN", "MQINI", "MQMONITOR", "MVSTCB", "NETNAME",
        "OSGIBUNDLE", "OSGISERVICE", "PARTNER", "PIPELINE", "PROCESSTYPE", "PROFILE", "PROGRAM", "REQID", "RRMS", "SECDISCOVERY",
        "SECRECORDING", "SECURITY", "STATISTICS", "STORAGE", "STREAMNAME", "SUBPOOL", "SYSDUMPCODE", "SYSTEM", "TASK",
        "TCLASS", "TCPIP", "TCPIPSERVICE", "TDQUEUE", "TEMPSTORAGE", "TERMINAL", "TRACEDEST", "TRACEFLAG", "TRACETYPE",
        "TRANCLASS", "TRANDUMPCODE", "TRANSACTION", "TSMODEL", "TSPOOL", "TSQNAME", "TSQUEUE", "UOW", "UOWDSNFAIL", "UOWENQ",
        "UOWLINK", "URIMAP", "VTAM", "WEB", "WEBSERVICE", "WLMHEALTH", "XMLTRANSFORM",
    };

    private static readonly string[] Lines =
    {
        "ABEND : ABCODE() CANCEL NODUMP",
        "ACQUIRE PROCESS() : PROCESSTYPE()",
        "ACQUIRE ACTIVITYID() :",
        "ADDRESS : ACEE() COMMAREA() CWA() EIB() TCTUA() TWA()",
        "ADDRESS SET() : USING()",
        "ALLOCATE : SYSID() SESSION() PARTNER() PROFILE() NOQUEUE STATE()",
        "ASKTIME : ABSTIME()",
        "~ASSIGN :",
        "BIF DEEDIT : FIELD() LENGTH()",
        "BIF DIGEST : RECORD() RECORDLEN() DIGESTTYPE() HEX BINARY BASE64 RESULT()",
        "BUILD ATTACH : ATTACHID() PROCESS() RESOURCE() RPROCESS() RRESOURCE() QUEUE() IUTYPE() DATASTR() RECFM()",
        "CANCEL : REQID() SYSID() TRANSID() ACTIVITY() ACQACTIVITY ACQPROCESS",
        "CHANGE PASSWORD() : NEWPASSWORD() USERID() ESMREASON() ESMRESP()",
        "CHANGE PHRASE() : PHRASELEN() NEWPHRASE() NEWPHRASELEN() USERID() ESMREASON() ESMRESP()",
        "CHANGE TASK : PRIORITY()",
        "CHECK ACTIVITY() : ACQACTIVITY ACQPROCESS COMPSTATUS() ABCODE() ABPROGRAM() MODE() SUSPSTATUS()",
        "CHECK ACQPROCESS : COMPSTATUS() ABCODE() ABPROGRAM() MODE() SUSPSTATUS()",
        "CHECK TIMER() : STATUS()",
        "CONNECT PROCESS : " + Appc + " PROCNAME() PROCLENGTH() PARTNER() SYNCLEVEL() PIPLIST() PIPLENGTH()",
        "CONVERSE : FROM() FROMLENGTH() FROMFLENGTH() INTO() SET() TOLENGTH() TOFLENGTH() MAXLENGTH() MAXFLENGTH() NOTRUNCATE ERASE DEFAULT ALTERNATE CTLCHAR() STRFIELD " + Appc + " DEST() LDC() LINEADDR() ASIS CNOTCOMPL DEFRESP FMH",
        "~CREATE :",
        "~CSD :",
        "DEFINE ACTIVITY() : TRANSID() PROGRAM() USERID() EVENT() ACTIVITYID()",
        "DEFINE PROCESS() : PROCESSTYPE() TRANSID() PROGRAM() USERID() NOCHECK",
        "DEFINE COUNTER() : POOL() VALUE() MINIMUM() MAXIMUM() NOSUSPEND",
        "DEFINE DCOUNTER() : POOL() VALUE() MINIMUM() MAXIMUM() NOSUSPEND",
        "DEFINE TIMER() : EVENT() AFTER AT ON DAYS() HOURS() MINUTES() SECONDS() YEAR() MONTH() DAYOFMONTH() DAYOFYEAR()",
        "DEFINE COMPOSITE EVENT() : AND OR SUBEVENT1() SUBEVENT2() SUBEVENT3() SUBEVENT4() SUBEVENT5() SUBEVENT6() SUBEVENT7() SUBEVENT8()",
        "DEFINE INPUT EVENT() :",
        "DELAY : " + TimeIntervals + " FOR UNTIL REQID()",
        "DELETE : " + FileKeys + " TOKEN() NUMREC() NOSUSPEND",
        "DELETE CONTAINER() : " + Containers,
        "DELETE ACTIVITY() :",
        "DELETE COUNTER() : POOL() NOSUSPEND",
        "DELETE DCOUNTER() : POOL() NOSUSPEND",
        "DELETE EVENT() :",
        "DELETE TIMER() :",
        "DELETEQ [TS] : QUEUE() QNAME() SYSID() MAIN AUXILIARY",
        "DELETEQ TD : QUEUE() SYSID()",
        "DEQ : RESOURCE() LENGTH() UOW TASK MAXLIFETIME()",
        "~DISCARD :",
        "DOCUMENT CREATE : DOCTOKEN() FROM() TEXT() BINARY() FROMDOC() TEMPLATE() SYMBOLLIST() LISTLENGTH() DELIMITER() UNESCAPED LENGTH() HOSTCODEPAGE() DOCSIZE()",
        "DOCUMENT DELETE : DOCTOKEN()",
        "DOCUMENT INSERT : DOCTOKEN() FROM() TEXT() BINARY() FROMDOC() TEMPLATE() SYMBOL() BOOKMARK() LENGTH() HOSTCODEPAGE() AT() TO() DOCSIZE()",
        "DOCUMENT RETRIEVE : DOCTOKEN() INTO() LENGTH() MAXLENGTH() CHARACTERSET() DATAONLY",
        "DOCUMENT SET : DOCTOKEN() SYMBOL() VALUE() SYMBOLLIST() DELIMITER() LENGTH() UNESCAPED",
        "DUMP TRANSACTION : DUMPCODE() FROM() LENGTH() FLENGTH() SEGMENTLIST() LENGTHLIST() NUMSEGMENTS() COMPLETE TRT TASK STORAGE PROGRAM TERMINAL TABLES FCT PCT PPT SIT TCT DUMPID()",
        "ENDBR : FILE() DATASET() REQID() SYSID()",
        "ENDBROWSE ACTIVITY : BROWSETOKEN()",
        "ENDBROWSE CONTAINER : BROWSETOKEN()",
        "ENDBROWSE EVENT : BROWSETOKEN()",
        "ENDBROWSE PROCESS : BROWSETOKEN()",
        "ENQ : RESOURCE() LENGTH() UOW TASK MAXLIFETIME() NOSUSPEND",
        "ENTER TRACENUM() : FROM() FROMLENGTH() RESOURCE() EXCEPTION",
        "~EXTRACT :",
        "FETCH CHILD() : CHANNEL() TIMEOUT() COMPSTATUS() ABCODE() ABPROGRAM() NOSUSPEND",
        "FETCH ANY() : CHANNEL() TIMEOUT() COMPSTATUS() ABCODE() ABPROGRAM() NOSUSPEND",
        "FORCE TIMER() : ACQPROCESS",
        "FORMATTIME : ABSTIME() DATE() FULLDATE() DATEFORM() DATESEP(?) DATESTRING() STRINGFORMAT() STRINGZONE() DAYCOUNT() DAYOFMONTH() DAYOFWEEK() DDMMYY() DDMMYYYY() MILLISECONDS() MMDDYY() MMDDYYYY() MONTHOFYEAR() TIME() TIMESEP(?) YEAR() YYDDD() YYDDMM() YYMMDD() YYYYDDD() YYYYDDMM() YYYYMMDD()",
        "FREE : " + Appc,
        "FREE CHILD() :",
        "FREEMAIN : DATA() DATAPOINTER()",
        "FREEMAIN64 : DATAPOINTER()",
        "GET CONTAINER() : " + Containers + " INTO() SET() FLENGTH() BYTEOFFSET() NODATA INTOCCSID() INTOCODEPAGE() CONVERTST()",
        "GET COUNTER() : POOL() VALUE() MINIMUM() MAXIMUM() INCREMENT() REDUCE WRAP COMPARE() EQUAL LESS GREATER NOSUSPEND",
        "GET DCOUNTER() : POOL() VALUE() MINIMUM() MAXIMUM() INCREMENT() REDUCE WRAP COMPARE() EQUAL LESS GREATER NOSUSPEND",
        "GETMAIN : SET() FLENGTH() LENGTH() INITIMG() BELOW USERDATAKEY CICSDATAKEY SHARED NOSUSPEND EXECUTABLE",
        "GETMAIN64 : SET() FLENGTH() LOCATION() USERDATAKEY CICSDATAKEY SHARED NOSUSPEND EXECUTABLE",
        "GETNEXT ACTIVITY() : BROWSETOKEN() LEVEL()",
        "GETNEXT CONTAINER() : BROWSETOKEN()",
        "GETNEXT EVENT() : BROWSETOKEN() COMPOSITE() EVENTTYPE() FIRESTATUS() PREDICATE() TIMER()",
        "GETNEXT PROCESS() : BROWSETOKEN() PROCESSTYPE()",
        "HANDLE ABEND : CANCEL PROGRAM() LABEL() RESET",
        "~HANDLE AID :",
        "~HANDLE CONDITION :",
        "~IGNORE CONDITION :",
        "~INQUIRE :",
        "~INVOKE APPLICATION :",
        "~INVOKE SERVICE :",
        "~INVOKE WEBSERVICE :",
        "~ISSUE :",
        "JOURNAL : JFILEID() JTYPEID() FROM() LENGTH() REQID() PREFIX() PFXLENG() STARTIO WAIT NOSUSPEND",
        "LINK : PROGRAM() COMMAREA() LENGTH() DATALENGTH() CHANNEL() INPUTMSG() INPUTMSGLEN() SYSID() SYNCONRETURN TRANSID()",
        "LINK ACQPROCESS : INPUTEVENT()",
        "LINK ACTIVITY() : ACQACTIVITY INPUTEVENT()",
        "LOAD : PROGRAM() SET() LENGTH() FLENGTH() ENTRY() HOLD",
        "MONITOR : POINT() DATA1() DATA2() ENTRYNAME()",
        "MOVE CONTAINER() : AS() CHANNEL() TOCHANNEL() FROMPROCESS FROMACTIVITY() TOPROCESS TOACTIVITY()",
        "POINT : " + Appc,
        "POP HANDLE :",
        "POST : " + TimeIntervals + " SET() REQID()",
        "PURGE MESSAGE :",
        "PUSH HANDLE :",
        "PUT CONTAINER() : " + Containers + " FROM() FLENGTH() BIT CHAR DATATYPE() FROMCCSID() FROMCODEPAGE() APPEND",
        "QUERY CHANNEL() : CONTAINERCNT()",
        "QUERY COUNTER() : POOL() VALUE() MINIMUM() MAXIMUM() NOSUSPEND",
        "QUERY DCOUNTER() : POOL() VALUE() MINIMUM() MAXIMUM() NOSUSPEND",
        "QUERY SECURITY : RESTYPE() RESCLASS() RESID() RESIDLENGTH() LOGMESSAGE() READ() UPDATE() CONTROL() ALTER() USERID() NOLOG LOG",
        "READ : " + FileKeys + " INTO() SET() LENGTH() DEBKEY DEBREC GTEQ EQUAL UPDATE TOKEN() UNCOMMITTED CONSISTENT REPEATABLE NOSUSPEND",
        "READNEXT : " + FileKeys + " INTO() SET() LENGTH() REQID() UPDATE TOKEN() NOSUSPEND UNCOMMITTED CONSISTENT REPEATABLE",
        "READPREV : " + FileKeys + " INTO() SET() LENGTH() REQID() UPDATE TOKEN() NOSUSPEND UNCOMMITTED CONSISTENT REPEATABLE",
        "READQ [TS] : QUEUE() QNAME() INTO() SET() LENGTH() NUMITEMS() ITEM() NEXT SYSID()",
        "READQ TD : QUEUE() INTO() SET() LENGTH() SYSID() NOSUSPEND",
        "RECEIVE : INTO() SET() LENGTH() FLENGTH() MAXLENGTH() MAXFLENGTH() NOTRUNCATE ASIS BUFFER LEAVEKB " + Appc + " PARTN()",
        "RECEIVE MAP() : MAPSET() INTO() SET() FROM() LENGTH() TERMINAL ASIS INPARTN() MAPPINGDEV()",
        "RECEIVE PARTN() : INTO() SET() LENGTH() ASIS",
        "RELEASE : PROGRAM()",
        "RESET ACTIVITY() : ACQACTIVITY",
        "RESETBR : " + FileKeys + " REQID() GTEQ EQUAL",
        "RESUME ACTIVITY() : ACQACTIVITY ACQPROCESS",
        "RETRIEVE : INTO() SET() LENGTH() RTRANSID() RTERMID() QUEUE() WAIT",
        "RETRIEVE REATTACH EVENT() : EVENTTYPE()",
        "RETRIEVE SUBEVENT() : EVENT() EVENTTYPE()",
        "RETURN : TRANSID() COMMAREA() LENGTH() CHANNEL() IMMEDIATE INPUTMSG() INPUTMSGLEN() ENDACTIVITY",
        "REWRITE : FILE() DATASET() FROM() LENGTH() SYSID() TOKEN() NOSUSPEND",
        "ROUTE : " + TimeIntervals + " ERRTERM(?) TITLE() LIST() OPCLASS() REQID() LDC() NLEOM",
        "RUN ACTIVITY() : ACQACTIVITY ASYNCHRONOUS SYNCHRONOUS INPUTEVENT()",
        "RUN ACQPROCESS : ASYNCHRONOUS SYNCHRONOUS INPUTEVENT()",
        "RUN TRANSID() : CHANNEL() CHILD()",
        "SEND : FROM() LENGTH() FLENGTH() WAIT INVITE LAST ERASE DEFAULT ALTERNATE CTLCHAR() STRFIELD CNOTCOMPL DEFRESP FMH " + Appc + " LDC() DEST() ASIS LINEADDR()",
        "SEND CONTROL : CURSOR(?) " + Paging,
        "SEND MAP() : MAPSET() FROM() DATAONLY MAPONLY LENGTH() CURSOR(?) MAPPINGDEV() " + Paging,
        "SEND PAGE : RELEASE RETAIN TRANSID() TRAILER() SET() AUTOPAGE NOAUTOPAGE OPERPURGE FMHPARM() LAST",
        "SEND PARTNSET : PARTNSET()",
        "SEND TEXT : FROM() LENGTH() CURSOR(?) HEADER() TRAILER() JUSTIFY() JUSFIRST JUSLAST NOEDIT MAPPED " + Paging,
        "~SET :",
        "SIGNAL EVENT() : FROMCHANNEL() FROM() FROMLENGTH()",
        "SIGNOFF :",
        "SIGNON : USERID() PASSWORD() NEWPASSWORD() PHRASE() PHRASELEN() NEWPHRASE() NEWPHRASELEN() ESMREASON() ESMRESP() GROUPID() LANGUAGECODE() LANGINUSE() NATLANG() NATLANGINUSE() OIDCARD() TERMINAL",
        "SOAPFAULT ADD : FROMCCSID() FAULTSTRING() FAULTSTRLEN() NATLANG() SUBCODESTR() SUBCODELEN() FAULTCODESTR() FAULTCODELEN()",
        "SOAPFAULT CREATE : CLIENT SERVER SENDER RECEIVER FAULTCODE() FAULTCODESTR() FAULTCODELEN() FAULTSTRING() FAULTSTRLEN() NATLANG() ROLE() ROLELENGTH() FAULTACTOR() FAULTACTLEN() DETAIL() DETAILLENGTH() FROMCCSID() SUBCODESTR() SUBCODELEN()",
        "SOAPFAULT DELETE :",
        "SPOOLCLOSE : TOKEN() KEEP DELETE NOHANDLE",
        "SPOOLOPEN INPUT : TOKEN() USERID() CLASS()",
        "SPOOLOPEN OUTPUT : TOKEN() USERID() NODE() CLASS() OUTDESCR() NOCC ASA MCC PRINT RECORDLENGTH() PUNCH",
        "SPOOLREAD : TOKEN() INTO() MAXFLENGTH() TOFLENGTH()",
        "SPOOLWRITE : TOKEN() FROM() FLENGTH() LINE PAGE",
        "START : TRANSID() " + TimeIntervals + " FROM() LENGTH() FMH TERMID() USERID() SYSID() RTRANSID() RTERMID() QUEUE() REQID() NOCHECK PROTECT CHANNEL()",
        "STARTBR : " + FileKeys + " REQID() DEBKEY DEBREC GTEQ EQUAL",
        "STARTBROWSE ACTIVITY : ACTIVITYID() PROCESS() PROCESSTYPE() BROWSETOKEN()",
        "STARTBROWSE CONTAINER : CHANNEL() ACTIVITY() ACQACTIVITY PROCESS ACQPROCESS BROWSETOKEN()",
        "STARTBROWSE EVENT : ACTIVITY() ACQACTIVITY BROWSETOKEN()",
        "STARTBROWSE PROCESS : PROCESSTYPE() BROWSETOKEN()",
        "SUSPEND :",
        "SUSPEND ACTIVITY() : ACQACTIVITY ACQPROCESS",
        "SYNCPOINT :",
        "SYNCPOINT ROLLBACK :",
        "TEST EVENT() : FIRESTATUS()",
        "~TRANSFORM :",
        "UNLOCK : FILE() DATASET() TOKEN() SYSID()",
        "UPDATE COUNTER() : POOL() VALUE() COMPAREMIN() COMPAREMAX() NOSUSPEND",
        "UPDATE DCOUNTER() : POOL() VALUE() COMPAREMIN() COMPAREMAX() NOSUSPEND",
        "VERIFY PASSWORD() : USERID() CHANGETIME() DAYSLEFT() ESMREASON() ESMRESP() EXPIRYTIME() INVALIDCOUNT() LASTUSETIME()",
        "VERIFY PHRASE() : PHRASELEN() USERID() CHANGETIME() DAYSLEFT() ESMREASON() ESMRESP() EXPIRYTIME() INVALIDCOUNT() LASTUSETIME()",
        "VERIFY TOKEN() : TOKENLEN() BASICAUTH JWT KERBEROS ESMREASON() ESMRESP() USERID() OUTTOKEN() OUTTOKENLEN()",
        "WAIT CONVID() : STATE()",
        "WAIT EVENT : ECADDR() NAME()",
        "WAIT EXTERNAL : ECBLIST() NUMEVENTS() PURGEABLE NOTPURGEABLE PURGEABILITY() NAME()",
        "WAIT JOURNALNAME() : REQID()",
        "WAIT JOURNALNUM() : REQID()",
        "WAIT SIGNAL :",
        "WAIT TERMINAL : " + Appc,
        "WAITCICS : ECBLIST() NUMEVENTS() PURGEABLE NOTPURGEABLE PURGEABILITY() NAME()",
        "~WEB :",
        "WRITE : " + FileKeys + " FROM() LENGTH() MASSINSERT NOSUSPEND",
        "WRITE JOURNALNAME() : JTYPEID() FROM() FLENGTH() REQID() PREFIX() PFXLENG() WAIT NOSUSPEND",
        "WRITE JOURNALNUM() : JTYPEID() FROM() FLENGTH() REQID() PREFIX() PFXLENG() WAIT NOSUSPEND",
        "WRITE OPERATOR : TEXT() TEXTLENGTH() ROUTECODES() NUMROUTES() ACTION() CRITICAL EVENTUAL IMMEDIATE REPLY() MAXLENGTH() REPLYLENGTH() TIMEOUT()",
        "WRITEQ [TS] : QUEUE() QNAME() FROM() LENGTH() NUMITEMS() ITEM() REWRITE SYSID() MAIN AUXILIARY NOSUSPEND",
        "WRITEQ TD : QUEUE() FROM() LENGTH() SYSID()",
        "XCTL : PROGRAM() COMMAREA() LENGTH() CHANNEL() INPUTMSG() INPUTMSGLEN()",
    };

    public static readonly IReadOnlyList<CicsCommandSpec> Commands = Build();

    // Commands indexed by their first verb word.
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<CicsCommandSpec>> ByFirstWord = Commands
        .GroupBy(c => c.Words[0], StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => (IReadOnlyList<CicsCommandSpec>)g.OrderByDescending(c => c.Words.Length).ToList(), StringComparer.Ordinal);

    // SPI resource types accepted after INQUIRE/SET/DISCARD/CREATE; the command is then e.g. "INQUIRE PROGRAM".
    public static readonly IReadOnlySet<string> SpiResourceTypes = new HashSet<string>(SpiResources, StringComparer.Ordinal);

    private static List<CicsCommandSpec> Build()
    {
        var list = new List<CicsCommandSpec>(Lines.Length);
        foreach (var line in Lines)
        {
            var open = line.StartsWith('~');
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var head = line[(open ? 1 : 0)..colon].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var words = new string[head.Length];
            var optionalQualifier = false;
            var verbArgument = NoArgument;
            for (var i = 0; i < head.Length; i++)
            {
                var (name, arg) = Split(head[i]);
                if (name.StartsWith('['))
                {
                    optionalQualifier = true;
                    name = name.Trim('[', ']');
                }

                words[i] = name;
                if (i == head.Length - 1)
                {
                    verbArgument = arg;
                }
            }

            var options = new Dictionary<string, char>(StringComparer.Ordinal);
            foreach (var item in line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var (name, arg) = Split(item);
                options[name] = arg;
            }

            list.Add(new CicsCommandSpec(words, optionalQualifier, verbArgument, options, open));
        }

        return list;
    }

    private static (string Name, char Argument) Split(string item)
        => item.EndsWith("(?)", StringComparison.Ordinal) ? (item[..^3], Optional)
            : item.EndsWith("()", StringComparison.Ordinal) ? (item[..^2], Required)
            : (item, NoArgument);
}
