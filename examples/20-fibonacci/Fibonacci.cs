using System;
class Fibonacci {
    static void Main() {
        int a = 0;
        int b = 1;
        int i = 0;
        while (i < 10) {
            Console.WriteLine(a);
            int next = a + b;
            a = b;
            b = next;
            i++;
        }
    }
}