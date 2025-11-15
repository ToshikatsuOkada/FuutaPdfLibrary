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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace FuutaSystemSvcCommonLibrary
{
    public class FSCLPdfChecker
    {

        public string FileName { get; set; }


        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// 表示用関数 / デフォルトでは読み捨て
        /// </summary>
        public Action<string?> DisplayMessage { get; set; } = (a) => { };


        public List<string> ResultText { get; } = new();

        /// <summary>
        /// 解析した文書を結合したもの(かなり細かく分割されているので、こっちを使うこと)
        /// </summary>
        public string JoinedResultText { get=>string.Join("", ResultText); }

        int currentPos = 0;


        public FSCLPdfChecker(string fname)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            FileName = fname;

            FileInfo finfo = new(fname);

            if (finfo.Exists)
            {
                Data = new byte[finfo.Length];
                using FileStream fs = finfo.Open(FileMode.Open, FileAccess.Read);

                int pos = 0;
                while (pos < Data.Length)
                {
                    pos += fs.Read(Data, pos, Data.Length - pos);
                }
            }

        }


        public void Clean()
        {
            Data = Array.Empty<byte>();
            ResultText.Clear();
        }



        public void Analyze()
        {
            // まず、先頭行を確認→ダメだったら終了
            if (!CheckHeader()) return;

            ResultText.Clear();

            ResultText.AddRange(CheckObj());
        }

        public enum ModeEnum
        {
            Normal,

            Table,

            ShiftJis,
        }

        /// <summary>
        /// オブジェクト情報のチェック
        /// </summary>
        /// <returns></returns>
        public List<string> CheckObj()
        {
            Dictionary<int, string> founds = new();

            int savedStartPos = currentPos;

            // Phase 1 : 変換テーブル作成(obj, obj, tableDict)
            Dictionary<int, Dictionary<int, Dictionary<ushort, ushort>>> tableDict = new();

            // Phase 2 : まずは、文字列（何が書いてあるかは不明）をひたすら解析
            Dictionary<string, List<KeyValuePair<int, List<byte>>>> decodeTargetBin = new();
            Dictionary<string, List<KeyValuePair<int, string>>> decodeTargetTxt = new();

            // F1 のフォントは obj の xx 番で定義
            Dictionary<string, int[]> FontTableDict = new();

            // obj 番のフォントが Unicode 変換テーブルのどの番号を使うか(サブ番号も考慮)
            Dictionary<int, Dictionary<int, int[]>> FontConvertDict = new();
            
            // Shift-Jis のフォント情報
            Dictionary<int, int> ShiftJisFont = new();


            int count = 0;

            while (true)
            {
                if (currentPos >= Data.Length)
                {
                    break;
                }

                string line = ReadLineAsString();

                if (line.Length == 0) continue;

                // コメント行はスキップ
                if (line[0] == '%') continue;

                DisplayMessage($"Read : {line}");

                try
                {
                    if (Regex.IsMatch(line, @"[0-9]+ +[0-9]+ +obj *$"))
                    {
                        Match numMatch = Regex.Match(line, @"[0-9]+");
                        int objnum1 = -1;
                        int objnum2 = -1;
                        if (numMatch.Success)
                        {
                            if (int.TryParse(numMatch.Value, out objnum1))
                            {
                                numMatch = numMatch.NextMatch();
                                if (numMatch.Success)
                                {
                                    int.TryParse(numMatch.Value, out objnum2);
                                }
                            }
                        }

                        // Object の先頭を見つけた
                        string line2 = ReadLineAsString();

                        DisplayMessage($"Read : {line2}");

                        // フォントごとの辞書番号を獲得
                        if (Regex.IsMatch(line2, @"/Font"))
                        {
                            // まず、フォント番号を調べる
                            Match fntMatch = Regex.Match(line2, @"/F[0-9]+ +[0-9]+ +[0-9]+");
                            while (fntMatch.Success)
                            {
                                string[] split = fntMatch.Value.Split(" ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                if ( split.Length == 3)
                                {
                                    int obnum1;
                                    if (int.TryParse(split[1], out obnum1))
                                    {
                                        int obnum2;
                                        if (int.TryParse(split[2], out obnum2))
                                        {
                                            FontTableDict.TryAdd(split[0].Substring(1), new int[] {obnum1, obnum2});
                                        }
                                    }
                                }
                                fntMatch = fntMatch.NextMatch();
                            }

                            // フォントが Unicode 変換テーブルを使っているか？
                            Match mch = Regex.Match(line2, @"/ToUnicode +[0-9]+ +[0-9]+");
                            if (mch.Success)
                            {
                                string[] sub = mch.Value.Split(" ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                                if ( sub.Length == 3)
                                {
                                    string no1str = sub[1];
                                    string no2str = sub[2];

                                    int no1;
                                    int no2;
                                    if (int.TryParse(no1str, out no1))
                                    {
                                        if (int.TryParse(no2str, out no2))
                                        {
                                            FontConvertDict.TryAdd(objnum1, new());
                                            FontConvertDict[objnum1].TryAdd(objnum2, new int[] { no1, no2 });
                                        }
                                    }
                                }
                            }

                            // フォントが Shift-Jis か？
                            Match sjis = Regex.Match(line2, @"RKSJ");
                            if (sjis.Success)
                            {
                                ShiftJisFont.Add(objnum1, objnum2);
                            }
                        }

                        if (line2.Contains("FlateDecode"))
                        {
                            Match lengthMatch = Regex.Match(line2, @"Length +[0-9]+");

                            int length = 0;

                            if (lengthMatch.Success)
                            {
                                Match value = Regex.Match(lengthMatch.Value, @"[0-9]+");
                                if (value.Success)
                                {
                                    if (int.TryParse(value.Value, out length))
                                    {
                                        // パース成功
                                        string line3 = ReadLineAsString();

                                        DisplayMessage($"Read : {line3}");

                                        if (line3.Contains("stream"))
                                        {
                                            // 先頭2byte読み飛ばすといいらしい
                                            byte[] streamData = new byte[length - 2];
                                            Array.Copy(Data, currentPos + 2, streamData, 0, length - 2);
                                            currentPos += length;

                                            // 読み飛ばしルールはここに(そうしないとバイナリを解析しようとしてしまう)
                                            if (Regex.IsMatch(line2, @"/XRef")) continue;
                                            if (Regex.IsMatch(line2, @"/ObjStm")) continue;
                                            if (Regex.IsMatch(line2, @"/Length1")) continue;
                                            if (Regex.IsMatch(line2, @"/XObject")) continue;
                                            if (Regex.IsMatch(line2, @"/DeviceRGB")) continue;
                                            if (Regex.IsMatch(line2, @"/DeviceGray")) continue;
                                            if (Regex.IsMatch(line2, @"/DeviceCMYK")) continue;
                                            if (Regex.IsMatch(line2, @"/CalGray")) continue;
                                            if (Regex.IsMatch(line2, @"/CalRGB")) continue;
                                            if (Regex.IsMatch(line2, @"/Lab")) continue;
                                            if (Regex.IsMatch(line2, @"/ICCCBased")) continue;
                                            if (Regex.IsMatch(line2, @"/Separation")) continue;
                                            if (Regex.IsMatch(line2, @"/Device")) continue;
                                            if (Regex.IsMatch(line2, @"/Indexed")) continue;
                                            if (Regex.IsMatch(line2, @"/Pattern")) continue;
                                            //if (Regex.IsMatch(line2, @"/ICCCBased")) continue;
                                            //if (Regex.IsMatch(line2, @"/ICCCBased")) continue;
                                            //if (Regex.IsMatch(line2, @"/ICCCBased")) continue;
                                            //if (Regex.IsMatch(line2, @"/ICCCBased")) continue;
                                            //if (Regex.IsMatch(line2, @"/ICCCBased")) continue;
                                            //if (Regex.IsMatch(line2, @"/ICCCBased")) continue;
                                            //if (Regex.IsMatch(line2, @"/ICCCBased")) continue;
                                            //if (Regex.IsMatch(line2, @"/ICCCBased")) continue;
                                            //if (Regex.IsMatch(line2, @"/ICCCBased")) continue;
                                            //if (Regex.IsMatch(line2, @"/ICCCBased")) continue;

                                            string[] line2array = line2.Replace("<<", "").Replace(">>", "").Split("/");

                                            int countLength = 0;
                                            int countFlateDecode = 0;
                                            int countFiler = 0;
                                            int countOther = 0;
                                            foreach(string elm in line2array)
                                            {
                                                string[] subElm = elm.Split(" ");
                                                if ( subElm.Length == 0 ) continue;
                                                if (subElm[0].Length == 0) continue;

                                                switch (subElm[0].ToLower())
                                                {
                                                    case "filter":
                                                        countFiler += 1;
                                                        break;

                                                    case "flatedecode":
                                                        countFlateDecode += 1;
                                                        break;

                                                    case "length":
                                                        countLength += 1;
                                                        break;

                                                    default:
                                                        countOther += 1;
                                                        break;
                                                }
                                            }
                                            if (countOther > 0)
                                            {
                                                continue;
                                            }

                                            DisplayMessage($"Decode as String!");

                                            // Stream をデコードする
                                            using MemoryStream mst = new(streamData);
                                            using DeflateStream deflateStream = new(mst, CompressionMode.Decompress);
                                            using MemoryStream output = new();
                                            deflateStream.CopyTo(output);
                                            byte[] result = output.GetBuffer();

                                            // 読み込んだ結果の文字列化
                                            //string readText = System.Text.Encoding.UTF8.GetString(result);
                                            string readText = System.Text.Encoding.GetEncoding("shift-jis").GetString(result);

                                            List<string> textsSub = new(readText.Split("\r\n"));
                                            List<string> texts = new();
                                            foreach (string t in textsSub)
                                            {
                                                foreach( string t1 in new List<string>(t.Split("\n")))
                                                {
                                                    texts.AddRange(t1.Split("\r"));
                                                }
                                            }

                                            bool start_beginbfchar = false;
                                            bool start_beginbfrange = false;

                                            List<List<byte[]>> lineBytes = new();

                                            string currentFont = string.Empty;

                                            foreach (string text in texts)
                                            {
                                                //DisplayMessage(text);

                                                if (Regex.IsMatch(text, @"beginbfchar"))
                                                {
                                                    start_beginbfchar = true;
                                                    continue;
                                                }

                                                if (Regex.IsMatch(text, "endbfchar"))
                                                {
                                                    start_beginbfchar = false;

                                                    // 辞書を出力する
                                                    foreach (List<byte[]> elm in lineBytes)
                                                    {
                                                        List<ushort> datas = ConvertByteArrayToUShortArray(elm);

                                                        if (datas.Count == 2)
                                                        {
                                                            tableDict.TryAdd(objnum1, new());
                                                            tableDict[objnum1].TryAdd(objnum2, new());

                                                            if (!tableDict[objnum1][objnum2].TryAdd(datas[0], datas[1]))
                                                            {
                                                                DisplayMessage($"Conflict : {datas[0]} : {tableDict[objnum1][objnum2][datas[0]]} vs {datas[1]}");
                                                                //tableDict[datas[0]] = datas[1];
                                                            }
                                                        }
                                                    }

                                                    lineBytes.Clear();

                                                    continue;
                                                }

                                                if (Regex.IsMatch(text, "beginbfrange"))
                                                {
                                                    start_beginbfrange = true;
                                                    continue;
                                                }

                                                if (Regex.IsMatch(text, "endbfrange"))
                                                {
                                                    start_beginbfrange = false;

                                                    // 辞書を出力する
                                                    foreach (List<byte[]> elm in lineBytes)
                                                    {
                                                        List<ushort> datas = ConvertByteArrayToUShortArray(elm);

                                                        tableDict.TryAdd(objnum1, new());
                                                        tableDict[objnum1].TryAdd(objnum2, new());

                                                        if (datas.Count == 3)
                                                        {
                                                            for (ushort i = 0; i <= datas[1] - datas[0]; i++)
                                                            {
                                                                if (!tableDict[objnum1][objnum2].TryAdd((ushort)(datas[0] + i), (ushort)(datas[2] + i)))
                                                                {
                                                                    DisplayMessage($"Conflict : {datas[0] + i} : {tableDict[objnum1][objnum2][(ushort)(datas[0] + i)]} vs {datas[2] + i}");
                                                                }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            for (ushort i = 0; i <= datas[1] - datas[0] && i < datas.Count - 2; i++)
                                                            {
                                                                if (!tableDict[objnum1][objnum2].TryAdd((ushort)(datas[0] + i), (ushort)(datas[2 + i])))
                                                                {
                                                                    DisplayMessage($"Conflict : {datas[0] + i} : {tableDict[objnum1][objnum2][(ushort)(datas[0] + i)]} vs {datas[2 + i]}");
                                                                }
                                                            }
                                                        }
                                                    }

                                                    lineBytes.Clear();

                                                    continue;
                                                }

                                                if (start_beginbfchar || start_beginbfrange)
                                                {
                                                    // 変換テーブル作成
                                                    Match mch = Regex.Match(text, @"\<[0-9a-fA-F]+\>");

                                                    List<byte[]> datas = new();

                                                    while (true)
                                                    {
                                                        if (!mch.Success) break;
                                                        string data = mch.Value;
                                                        if (data.Length < 2) continue;
                                                        string data2 = data.Substring(1, data.Length - 2);
                                                        if (data2.Length == 0) continue;
                                                        // 奇数対応
                                                        string data3 = data2 + "0";

                                                        List<byte> bytes = new();

                                                        for (int pos = 0; pos < data2.Length; pos += 2)
                                                        {
                                                            bytes.Add(Convert.ToByte(data3.Substring(pos, 2), 16));
                                                        }

                                                        datas.Add(bytes.ToArray());

                                                        mch = mch.NextMatch();
                                                    }

                                                    if (datas.Count > 0)
                                                    {
                                                        lineBytes.Add(datas);
                                                    }

                                                }

                                                if (Regex.IsMatch(text, @"/F[0-9]+ "))
                                                {
                                                    DisplayMessage(text);
                                                    Match fontMatch = Regex.Match(text, @"/F[0-9]+ ");
                                                    if (fontMatch.Success)
                                                    {
                                                        Match fontMatch2 = Regex.Match(fontMatch.Value, @"F[0-9]+");
                                                        if (fontMatch2.Success)
                                                        {
                                                            currentFont = fontMatch2.Value;
                                                        }
                                                    }
                                                }

                                                if (Regex.IsMatch(text, @"T[Jj]$"))
                                                {
                                                    //ModeEnum mode = ModeEnum.Normal;
                                                    //if ( Regex.IsMatch(text, @"TJ$"))
                                                    //{
                                                    //    mode = ModeEnum.Table;
                                                    //}

                                                    DisplayMessage(text);

                                                    // まず、 <> の 16進対応
                                                    {
                                                        Match hex = Regex.Match(text, @"\<[0-9a-fA-F]*\>");

                                                        List<byte> bytes = new();

                                                        while (true)
                                                        {
                                                            if (!hex.Success) break;

                                                            if (hex.Value.Length >= 2)
                                                            {
                                                                string hx = hex.Value.Substring(1, hex.Value.Length - 2);

                                                                for (int pos = 0; pos < hx.Length; pos += 2)
                                                                {
                                                                    if (pos + 1 >= hx.Length)
                                                                    {
                                                                        // 奇数だから飛ばす
                                                                        break;
                                                                    }
                                                                    bytes.Add(Convert.ToByte(hx.Substring(pos, 2), 16));
                                                                }

                                                                // UTF16変換
                                                                //string cvTxt1 = Encoding.Unicode.GetString(array);
                                                                //string cvTxt2 = Encoding.BigEndianUnicode.GetString(array);
                                                                //founds.Add(cvTxt1);
                                                                //founds.Add(cvTxt2);
                                                                //DisplayMessage(cvTxt1);
                                                                //DisplayMessage(cvTxt2);
                                                            }

                                                            hex = hex.NextMatch();
                                                        }

                                                        if (bytes.Count > 0)
                                                        {
                                                            Match td = Regex.Match(text, @"Td");
                                                            //if ((td.Success) && (bytes.Count == 1))
                                                            //{
                                                            //    // デコードしちゃだめ
                                                            //}
                                                            //else
                                                            //{
                                                            //    decodeTargetBin.TryAdd(currentFont, new());
                                                            //    decodeTargetBin[currentFont].Add(new KeyValuePair<int, List<byte>>(count, bytes));
                                                            //}

                                                            decodeTargetBin.TryAdd(currentFont, new());
                                                            decodeTargetBin[currentFont].Add(new KeyValuePair<int, List<byte>>(count, bytes));

                                                            count += 1;
                                                        }
                                                    }


                                                    // 続いて PlainText の対応
                                                    {
                                                        Match unit = Regex.Match(text, @"\(.*\)");

                                                        while (true)
                                                        {
                                                            if (!unit.Success) break;

                                                            string text2 = Regex.Replace(unit.Value, @"\)[0-9\-]*\(", "");

                                                            if (text2.Length >= 2)
                                                            {
                                                                string txt = text2.Substring(1, text2.Length - 2);
                                                                decodeTargetTxt.TryAdd(currentFont, new());
                                                                decodeTargetTxt[currentFont].Add(new KeyValuePair<int, string>(count, txt));
                                                                count += 1;
                                                            }

                                                            unit = unit.NextMatch();
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else if (Regex.IsMatch(line2, @"Length +[0-9]+"))
                        {
                            Match lengthMatch = Regex.Match(line2, @"Length +[0-9]+");
                            Match value = Regex.Match(lengthMatch.Value, @"[0-9]+");
                            int length = 0;

                            if (value.Success)
                            {
                                if (int.TryParse(value.Value, out length))
                                {
                                    // パース成功
                                    string line3 = ReadLineAsString();

                                    DisplayMessage($"Read : {line3}");

                                    if (line3.Contains("stream"))
                                    {
                                        // 解読もしないのでそのまんま
                                        currentPos += length;
                                    }
                                }

                                DisplayMessage($"Current Pos : {currentPos}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DisplayMessage(ex.Message);
                    DisplayMessage(ex.StackTrace ?? string.Empty);
                }
            }

            //currentPos = savedStartPos;

            // ここから、変換
            foreach(string font in decodeTargetBin.Keys)
            {
                if (FontTableDict.ContainsKey(font))
                {
                    int obj1 = FontTableDict[font][0];
                    int obj2 = FontTableDict[font][1];

                    if (FontConvertDict.ContainsKey(obj1))
                    {
                        if (FontConvertDict[obj1].ContainsKey(obj2))
                        {
                            int table1 = FontConvertDict[obj1][obj2][0];
                            int table2 = FontConvertDict[obj1][obj2][1];

                            Dictionary<ushort, ushort>? table = null;

                            if (tableDict.ContainsKey(table1))
                            {
                                if (tableDict[table1].ContainsKey(table2))
                                {
                                    table = tableDict[table1][table2];
                                }
                            }
 
                            foreach (KeyValuePair<int, List<byte>> bytes in decodeTargetBin[font])
                            {
                                List<byte> results = new();

                                List<byte> bytesMod =new List<byte>(bytes.Value);
                                if ( bytesMod.Count % 2 == 1)
                                {
                                    bytesMod.Add(0);
                                }

                                if ( table != null)
                                {
                                    for (int i = 0; i < bytesMod.Count; i += 2)
                                    {
                                        byte[] cnv = new byte[] { bytesMod[i + 1], bytesMod[i] };
                                        ushort sv = BitConverter.ToUInt16(cnv, 0);

                                        ushort val;
                                        if (table.TryGetValue(sv, out val))
                                        {
                                            results.Add((byte)(val >> 8));
                                            results.Add((byte)(val & 0x00ff));
                                        }
                                        else
                                        {
                                            DisplayMessage("Bad!!");
                                        }
                                    }
                                }
                                else
                                {
                                    results.AddRange(bytes.Value);
                                }

                                string cvTxt = Encoding.BigEndianUnicode.GetString(results.ToArray());
                                if (cvTxt.Length > 0)
                                {
                                    founds.Add(bytes.Key, cvTxt);
                                }
                            }

                            // 明示的にメモリ解放
                            table?.Clear();
                        }
                        else
                        {
                            foreach (KeyValuePair<int, List<byte>> bytes in decodeTargetBin[font])
                            {
                                string cvTxt = Encoding.BigEndianUnicode.GetString(bytes.Value.ToArray());
                                if (cvTxt.Length > 0)
                                {
                                    founds.Add(bytes.Key, cvTxt);
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (KeyValuePair<int, List<byte>> bytes in decodeTargetBin[font])
                        {
                            string cvTxt = Encoding.BigEndianUnicode.GetString(bytes.Value.ToArray());
                            if (cvTxt.Length > 0)
                            {
                                founds.Add(bytes.Key, cvTxt);
                            }
                        }
                    }
                }
                else
                {
                    //foreach (KeyValuePair<int, List<byte>> bytes in decodeTargetBin[font])
                    //{
                    //    string cvTxt = Encoding.BigEndianUnicode.GetString(bytes.Value.ToArray());
                    //    if (cvTxt.Length > 0)
                    //    {
                    //        founds.Add(bytes.Key, cvTxt);
                    //    }
                    //}
                }
            }


            foreach (string font in decodeTargetTxt.Keys)
            {
                if (FontTableDict.ContainsKey(font))
                {
                    int obj1 = FontTableDict[font][0];
                    int obj2 = FontTableDict[font][1];

                    ModeEnum mode = ModeEnum.Normal;

                    if ( ShiftJisFont.ContainsKey(obj1))
                    {
                        if (ShiftJisFont[obj1] == obj2)
                        {
                            mode = ModeEnum.ShiftJis;
                        }
                    }

                    if (FontConvertDict.ContainsKey(obj1))
                    {
                        if (FontConvertDict[obj1].ContainsKey(obj2))
                        {
                            mode = ModeEnum.Table;
                        }
                    }

                    if (mode == ModeEnum.Table)
                    {
                        int table1 = FontConvertDict[obj1][obj2][0];
                        int table2 = FontConvertDict[obj1][obj2][1];

                        Dictionary<ushort, ushort>? table = null;

                        if (tableDict.ContainsKey(table1))
                        {
                            if (tableDict[table1].ContainsKey(table2))
                            {
                                table = tableDict[table1][table2];
                            }
                        }

                        foreach (KeyValuePair<int, string> txt in decodeTargetTxt[font])
                        {
                            string? addTxt = string.Empty;

                            if (table != null)
                            {
                                if (txt.Value.Length > 0)
                                {
                                    foreach (char c in txt.Value)
                                    {
                                        ushort v = c;
                                        ushort cv;
                                        if (table.TryGetValue(v, out cv))
                                        {
                                            addTxt += (char)cv;
                                        }
                                        else
                                        {
                                            DisplayMessage($"Can't convert char = {(int)c}");
                                            //addTxt += c;
                                            //break;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                addTxt = txt.Value;
                            }

                            if (addTxt != null)
                            {
                                founds.Add(txt.Key, addTxt);
                            }
                        }

                        // 明示的なメモリ解放
                        table?.Clear();
                    }
                    else if (mode == ModeEnum.ShiftJis)
                    {
                        // Shift-JIS のエンコーディングを取得
                        foreach (KeyValuePair<int, string> txt in decodeTargetTxt[font])
                        {
                            string addTxt = txt.Value;

                            founds.Add(txt.Key, addTxt);
                        }
                    }
                    else
                    {
                        foreach (KeyValuePair<int, string> txt in decodeTargetTxt[font])
                        {
                            founds.Add(txt.Key, txt.Value);
                        }
                    }
                }
                else
                {
                    //foreach (KeyValuePair<int, string> txt in decodeTargetTxt[font])
                    //{
                    //    founds.Add(txt.Key, txt.Value);
                    //}
                }
            }

            List<int> keys = new(founds.Keys);
            keys.Sort();
            
            List<string> ret = new();
            foreach (int key in keys)
            {
                ret.Add(founds[key]);
            }

            // 明示的なメモリ解放処理
            tableDict.Values.ToList().ForEach(x=>x.Clear());
            tableDict.Clear();
            decodeTargetBin.Values.ToList().ForEach(x => x.ForEach(y => y.Value.Clear()));
            decodeTargetBin.Values.ToList().ForEach(x => x.Clear());
            decodeTargetBin.Clear();
            decodeTargetTxt.Values.ToList().ForEach(x => x.Clear());
            decodeTargetTxt.Clear();
            FontTableDict.Clear();
            FontConvertDict.Values.ToList().ForEach(x => x.Clear());
            FontConvertDict.Clear();
            ShiftJisFont.Clear();

            return ret;
        }

        /// <summary>
        /// byte配列を short 配列に変換する
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private List<ushort> ConvertByteArrayToUShortArray(List<byte[]> elm)
        {
            List<ushort> datas = new();
            foreach (byte[] b in elm)
            {
                if (b.Length == 1)
                {
                    byte[] cnv = new byte[] { b[0], 0 };

                    ushort sv = BitConverter.ToUInt16(cnv, 0);

                    datas.Add(sv);
                }
                else
                {
                    for (int i = 0; i < b.Length; i += 2)
                    {
                        if (i + 1 >= b.Length) continue;

                        byte[] cnv = new byte[] { b[i + 1], b[i] };

                        ushort sv = BitConverter.ToUInt16(cnv, 0);

                        datas.Add(sv);
                    }
                }
            }

            return datas;
        }



        /// <summary>
        /// ヘッダ情報のチェック
        /// </summary>
        /// <returns></returns>
        public bool CheckHeader()
        {
            bool ret = false;

            string line = ReadLineAsString();
            if (Regex.IsMatch(line, @"^\%PDF-[0-9].[0-9].*$"))
            {
                DisplayMessage($"OK : {line}");
                ret = true;
            }
            else
            {
                DisplayMessage($"NG : {line}");
            }

            return ret;
        }


        public string ReadLineAsString()
        {
            string ret = string.Empty;

            while (true)
            {
                string? result = ReadLineAsStringSub();

                if (result == null) break;

                ret += result;

                int count1 = Regex.Matches(ret, @"\<\<").Count;
                int count2 = Regex.Matches(ret, @"\>\>").Count;

                if (count1 == count2) break;
            }

            return ret;
        }

        public string? ReadLineAsStringSub()
        {
            string str = string.Empty;
            string str2 = string.Empty;

            while (true)
            {
                byte[]? line = ReadLine();

                if (line == null) return null;

                str = Encoding.UTF8.GetString(line);
                str2 = str.Replace("\r\n", "").Replace("\r", "").Replace("\n", "");

                if (str2.Length > 0) break;
                if (str.Length == 0 && str2.Length == 0) break;
            }

            return str2;
        }


        /// <summary>
        /// PDF解析
        /// </summary>
        public byte[]? ReadLine()
        {
            int startPos = currentPos;
            int length = 0;

            byte? prev = null;

            if (startPos >= Data.Length) return null;

            while (true)
            {
                if (startPos + length >= Data.Length) break;

                if (prev != null)
                {
                    if (prev == 0x0d && Data[startPos + length] == 0x0a)
                    {
                        // 最終行まで来た
                        length += 1;
                        break;
                    }
                    else if (prev == 0x0d)
                    {
                        // 最終行まで来た(前の文字で)
                        break;
                    }
                    else if (prev == 0x0a)
                    {
                        // 最終行まで来た(前の文字で)
                        break;
                    }

                    if (prev == '>' && Data[startPos + length] == '>')
                    {
                        // これはここで改行したのと同じ扱いと同じ扱いにする
                        length += 1;
                        break;
                    }

                    // 次の 2文字が << ならば改行と判断
                    if ( startPos + length + 2 < Data.Length)
                    {
                        if (Data[startPos + length + 1] == '<' && Data[startPos + length + 2] == '<')
                        {
                            length += 1;
                            break;
                        }
                    }
                }
                prev = Data[startPos + length];
                length += 1;
            }

            byte[] ret = new byte[length];

            Array.Copy(Data, startPos, ret, 0, length);

            // 読み込み位置情報を進める
            currentPos += length;

            return ret;
        }

    }
}
