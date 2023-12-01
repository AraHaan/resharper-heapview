﻿using System;

// empty
{
  Span<int> empty1 = [];
  ReadOnlySpan<int> empty2 = [];
}

// bytes
{
  Span<byte> byte1 = [1]; // heap array, mutable
  ReadOnlySpan<byte> byte2 = [2]; // static data
}

// non bytes
{
  Span<int> int1 = [1]; // heap array, mutable
  ReadOnlySpan<int> int2 = [2]; // createspan helper
}