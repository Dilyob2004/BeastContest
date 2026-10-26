using System;

var numbers = Array.ConvertAll(Console.ReadLine()!.Split(), long.Parse);
Console.WriteLine(numbers[0] + numbers[1]);
