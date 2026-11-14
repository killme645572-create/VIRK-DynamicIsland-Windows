using System;
using System.Collections.Generic;
using System.Text;

namespace Island
{
    // Генератор QR-кода без сторонних пакетов: байтовый режим (UTF-8), коррекция ошибок M, версии 1–40.
    // Логика проверена на прототипе: все 40 версий декодируются OpenCV.
    public static class QrCode
    {
        static readonly int[] EccPerBlock = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 };
        static readonly int[] NumBlocks   = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 };

        static int RawModules(int ver)
        {
            int r = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int na = ver / 7 + 2;
                r -= (25 * na - 10) * na - 55;
                if (ver >= 7) r -= 36;
            }
            return r;
        }

        static int DataCodewords(int ver) => RawModules(ver) / 8 - EccPerBlock[ver] * NumBlocks[ver];

        // ---------- Рид-Соломон ----------

        static int RsMul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z;
        }

        static int[] RsDivisor(int deg)
        {
            var res = new int[deg];
            res[deg - 1] = 1;
            int root = 1;
            for (int i = 0; i < deg; i++)
            {
                for (int j = 0; j < deg; j++)
                {
                    res[j] = RsMul(res[j], root);
                    if (j + 1 < deg) res[j] ^= res[j + 1];
                }
                root = RsMul(root, 2);
            }
            return res;
        }

        static int[] RsRemainder(int[] data, int[] div)
        {
            var res = new int[div.Length];
            foreach (int b in data)
            {
                int f = b ^ res[0];
                Array.Copy(res, 1, res, 0, res.Length - 1);
                res[res.Length - 1] = 0;
                for (int i = 0; i < div.Length; i++) res[i] ^= RsMul(div[i], f);
            }
            return res;
        }

        // ---------- кодирование ----------

        // Возвращает матрицу [y, x] (true = тёмный модуль) или null, если текст не помещается.
        public static bool[,] Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);

            int ver = 1;
            while (true)
            {
                if (ver > 40) return null;
                int ccBits = ver < 10 ? 8 : 16;
                if (4 + ccBits + data.Length * 8 <= DataCodewords(ver) * 8) break;
                ver++;
            }
            int cc = ver < 10 ? 8 : 16;

            var bits = new List<int>();
            void Put(int v, int n) { for (int i = n - 1; i >= 0; i--) bits.Add((v >> i) & 1); }

            Put(4, 4);
            Put(data.Length, cc);
            foreach (byte b in data) Put(b, 8);
            int cap = DataCodewords(ver) * 8;
            Put(0, Math.Min(4, cap - bits.Count));
            while (bits.Count % 8 != 0) bits.Add(0);
            int pad = 0xEC;
            while (bits.Count < cap) { Put(pad, 8); pad ^= 0xEC ^ 0x11; }

            var cw = new int[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++) cw[i >> 3] |= bits[i] << (7 - (i & 7));

            // коррекция ошибок и перемешивание блоков
            int nb = NumBlocks[ver], eccLen = EccPerBlock[ver];
            int raw = RawModules(ver) / 8;
            int shortBlocks = nb - raw % nb;
            int shortLen = raw / nb;
            var div = RsDivisor(eccLen);
            var blocks = new List<int[]>();
            int k = 0;
            for (int i = 0; i < nb; i++)
            {
                int n = shortLen - eccLen + (i < shortBlocks ? 0 : 1);
                var dat = new int[n];
                Array.Copy(cw, k, dat, 0, n);
                k += n;
                var ecc = RsRemainder(dat, div);
                int datLen = i < shortBlocks ? n + 1 : n;   // у коротких блоков один пустой байт для выравнивания
                var blk = new int[datLen + eccLen];
                Array.Copy(dat, blk, n);
                Array.Copy(ecc, 0, blk, datLen, eccLen);
                blocks.Add(blk);
            }
            var outCw = new List<int>();
            for (int i = 0; i < blocks[0].Length; i++)
                for (int j = 0; j < blocks.Count; j++)
                    if (i != shortLen - eccLen || j >= shortBlocks)
                        outCw.Add(blocks[j][i]);

            // матрица
            int size = ver * 4 + 17;
            var mod = new bool[size, size];
            var fn = new bool[size, size];

            for (int i = 0; i < size; i++)
            {
                SetF(mod, fn, 6, i, i % 2 == 0);
                SetF(mod, fn, i, 6, i % 2 == 0);
            }
            Finder(mod, fn, size, 3, 3);
            Finder(mod, fn, size, size - 4, 3);
            Finder(mod, fn, size, 3, size - 4);

            var pos = AlignPositions(ver, size);
            int na = pos.Count;
            for (int i = 0; i < na; i++)
                for (int j = 0; j < na; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == na - 1) || (i == na - 1 && j == 0)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            SetF(mod, fn, pos[i] + dx, pos[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            DrawFormat(mod, fn, size, 0);
            DrawVersion(mod, fn, size, ver);

            // данные змейкой по двойным колонкам
            int idx = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (!fn[y, x] && idx < outCw.Count * 8)
                        {
                            mod[y, x] = ((outCw[idx >> 3] >> (7 - (idx & 7))) & 1) != 0;
                            idx++;
                        }
                    }
            }

            // выбираем маску с минимальным штрафом
            int bestMask = 0, bestPenalty = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                ApplyMask(mod, fn, size, m);
                DrawFormat(mod, fn, size, m);
                int p = Penalty(mod, size);
                if (p < bestPenalty) { bestPenalty = p; bestMask = m; }
                ApplyMask(mod, fn, size, m);   // откат
            }
            ApplyMask(mod, fn, size, bestMask);
            DrawFormat(mod, fn, size, bestMask);
            return mod;
        }

        static void SetF(bool[,] mod, bool[,] fn, int x, int y, bool dark)
        {
            mod[y, x] = dark;
            fn[y, x] = true;
        }

        static void Finder(bool[,] mod, bool[,] fn, int size, int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < size && y >= 0 && y < size) SetF(mod, fn, x, y, d != 2 && d != 4);
                }
        }

        static List<int> AlignPositions(int ver, int size)
        {
            var res = new List<int>();
            if (ver == 1) return res;
            int na = ver / 7 + 2;
            int step = ver == 32 ? 26 : (ver * 4 + na * 2 + 1) / (na * 2 - 2) * 2;
            res.Add(6);
            int p = size - 7;
            while (res.Count < na) { res.Insert(1, p); p -= step; }
            return res;
        }

        static bool Bit(int x, int i) => ((x >> i) & 1) != 0;

        static void DrawFormat(bool[,] mod, bool[,] fn, int size, int mask)
        {
            int data = (0 << 3) | mask;   // уровень M кодируется нулём
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            for (int i = 0; i < 6; i++) SetF(mod, fn, 8, i, Bit(bits, i));
            SetF(mod, fn, 8, 7, Bit(bits, 6));
            SetF(mod, fn, 8, 8, Bit(bits, 7));
            SetF(mod, fn, 7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) SetF(mod, fn, 14 - i, 8, Bit(bits, i));

            for (int i = 0; i < 8; i++) SetF(mod, fn, size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) SetF(mod, fn, 8, size - 15 + i, Bit(bits, i));
            SetF(mod, fn, 8, size - 8, true);
        }

        static void DrawVersion(bool[,] mod, bool[,] fn, int size, int ver)
        {
            if (ver < 7) return;
            int rem = ver;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = (ver << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool b = Bit(bits, i);
                int a = size - 11 + i % 3, c = i / 3;
                SetF(mod, fn, a, c, b);
                SetF(mod, fn, c, a, b);
            }
        }

        static void ApplyMask(bool[,] mod, bool[,] fn, int size, int m)
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool inv;
                    switch (m)
                    {
                        case 0: inv = (x + y) % 2 == 0; break;
                        case 1: inv = y % 2 == 0; break;
                        case 2: inv = x % 3 == 0; break;
                        case 3: inv = (x + y) % 3 == 0; break;
                        case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: inv = x * y % 2 + x * y % 3 == 0; break;
                        case 6: inv = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: inv = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (inv && !fn[y, x]) mod[y, x] = !mod[y, x];
                }
        }

        // упрощённый штраф: длинные серии, блоки 2×2 и баланс тёмных/светлых
        static int Penalty(bool[,] mod, int size)
        {
            int p = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < size; a++)
                {
                    int run = 1;
                    for (int b = 1; b < size; b++)
                    {
                        bool cur = pass == 0 ? mod[a, b] : mod[b, a];
                        bool prv = pass == 0 ? mod[a, b - 1] : mod[b - 1, a];
                        if (cur == prv)
                        {
                            run++;
                            if (run == 5) p += 3;
                            else if (run > 5) p += 1;
                        }
                        else run = 1;
                    }
                }
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                {
                    bool c = mod[y, x];
                    if (c == mod[y, x + 1] && c == mod[y + 1, x] && c == mod[y + 1, x + 1]) p += 3;
                }
            int dark = 0;
            foreach (bool v in mod) if (v) dark++;
            int total = size * size;
            int kk = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            p += Math.Max(0, kk) * 10;
            return p;
        }
    }
}
