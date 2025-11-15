// Copyright 2025.11.16 Fuuta System Service LLC./Toshikatsu Okada
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

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


