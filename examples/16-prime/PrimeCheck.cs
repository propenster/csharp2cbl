using System;
class PrimeCheck {
    static void Main() {
        int n = 29;
        bool isPrime = true;
        int i = 2;
        while (i * i <= n) {
            if (n % i == 0) isPrime = false;
            i++;
        }
        if (isPrime) Console.WriteLine("prime");
        else         Console.WriteLine("not prime");
    }
}