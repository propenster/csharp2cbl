using System;
class TempTable {
    static void Main() {
        int c = 0;
        while (c <= 100) {
            int f = c * 9 / 5 + 32;
            Console.WriteLine(f);
            c = c + 20;
        }
    }
}