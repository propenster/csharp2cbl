using System;
class SumLoop {
    static void Main() {
        int i = 1;
        int sum = 0;
        while (i <= 10) { sum = sum + i; i++; }
        Console.WriteLine(sum);
    }
}