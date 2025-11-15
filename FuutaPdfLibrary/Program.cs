using System.IO;
using FuutaSystemSvcCommonLibrary;

if ( args.Length != 1)
{
    Console.WriteLine("引数にPDFファイルのパスを一つ指定してください。");
    return;
}


string fname = args[0];

FileInfo finfo = new(fname);

if (finfo.Exists)
{
    FSCLPdfChecker pdfChecker = new(fname);

    //デバッグするときだけこのコメントを外す
    //pdfChecker.DisplayMessage = (a) => Console.WriteLine(a);

    pdfChecker.Analyze();

    List<string> texts = new(pdfChecker.ResultText);

    foreach (string text in texts)
    {
        Console.WriteLine(text);
    }
}
else
{
    Console.WriteLine("指定されたPDFファイルが存在しません。");
}


