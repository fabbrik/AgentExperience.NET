package agentexperience

import "math"

// exp2 is 2^x correctly rounded (to nearest, ties to even), for ranking's recency and confidence decay.
//
// .NET's Math.Pow(2, x) is the platform C library's pow, which is not correctly rounded on every input: glibc's was
// measured 1 ulp off on about 0.07% of x in (-60, 0) (about 1 in 1,500), macOS's on about 0.12%. Go's math.Pow is
// further off (about 30% of inputs by an ulp or more; math.Pow(2, -803580.0/2592000) is 2 ulps above), and ranking
// orders records on the last bit of a score. A correctly rounded 2^x agrees with .NET's pow except on those rare
// inputs; contract 1.18's retrieval-policy/ranking-near-tie pins only its own inputs, which every one of them gives
// alike. Near-tied records whose recency or decay falls on a misrounded input may still rank differently from .NET.
//
// It splits x into an integer k and a fraction f in [-1/2, 1/2] (exactly), evaluates 2^f = e^(f ln 2) in double-double
// arithmetic (about 104 bits, so the one rounding at the end is correct unless 2^f lies within about 2^-100 of a
// midpoint between two doubles), and scales by 2^k. The scaling is exact, so the result is rounded once: a normal
// result is the double-double's rounding scaled by 2^k; a subnormal one is rounded once, from the double-double, to
// a whole multiple of 2^-1074.
func exp2(x float64) float64 {
	switch {
	case math.IsNaN(x):
		return x
	case x == 0:
		return 1
	case x > 1024:
		return math.Inf(1)
	case x < -1080:
		return 0
	}
	k := math.Round(x)
	f := x - k // exact: k is the integer nearest x
	// t = f * ln 2, in double-double.
	t := ddMulDouble(dd{hi: ln2Hi, lo: ln2Lo}, f)
	// e^t by Horner's rule on its Taylor series, |t| <= 0.347: the 25th term is below 2^-120 of the sum.
	sum := dd{hi: 1}
	for n := 24; n >= 1; n-- {
		sum = ddAddOne(ddDivInt(ddMul(t, sum), float64(n)))
	}
	if k > -1022 {
		// 2^f is in [0.707, 1.415), so the result is at least 2^-1021.5: normal, and the scaling exact.
		return math.Ldexp(sum.hi+sum.lo, int(k))
	}
	// Possibly subnormal: count in units of 2^-1074. Scaling both halves by 2^(k+1074) >= 2^-6 is exact, and the
	// count is below 2^53, so rounding it to a whole number is the result's one rounding.
	units := dd{hi: math.Ldexp(sum.hi, int(k)+1074), lo: math.Ldexp(sum.lo, int(k)+1074)}
	n := math.RoundToEven(units.hi)
	// units.hi - n is exact. Only when units.hi is itself a midpoint can the low half move the count, and then its
	// sign alone decides: |lo| is at most half an ulp of units.hi, never enough to reach another midpoint.
	switch remainder := units.hi - n; {
	case remainder == 0.5 && units.lo > 0:
		n++
	case remainder == -0.5 && units.lo < 0:
		n--
	}
	return math.Ldexp(n, -1074)
}

// ln 2 to 107 bits, as the sum of two doubles.
const (
	ln2Hi = 0x1.62e42fefa39efp-01
	ln2Lo = 0x1.abc9e3b39803fp-56
)

// dd is an unevaluated sum hi + lo with |lo| <= ulp(hi)/2.
type dd struct{ hi, lo float64 }

// twoSum is a + b as a double and its exact rounding error.
func twoSum(a, b float64) dd {
	s := a + b
	bb := s - a
	return dd{hi: s, lo: (a - (s - bb)) + (b - bb)}
}

// quickTwoSum is twoSum for |a| >= |b|.
func quickTwoSum(a, b float64) dd {
	s := a + b
	return dd{hi: s, lo: b - (s - a)}
}

// twoProd is a * b as a double and its exact rounding error. The conversion rounds the product where it is
// computed: without it, Go may fuse the product into an expression that uses p (ddDivInt's a.hi - p.hi), which then
// sees the exact product and counts its error twice.
func twoProd(a, b float64) dd {
	p := float64(a * b)
	return dd{hi: p, lo: math.FMA(a, b, -p)}
}

func ddMul(a, b dd) dd {
	p := twoProd(a.hi, b.hi)
	return quickTwoSum(p.hi, p.lo+(a.hi*b.lo+a.lo*b.hi))
}

func ddMulDouble(a dd, b float64) dd {
	p := twoProd(a.hi, b)
	return quickTwoSum(p.hi, p.lo+a.lo*b)
}

func ddDivInt(a dd, n float64) dd {
	q := a.hi / n
	p := twoProd(q, n)
	return quickTwoSum(q, (((a.hi-p.hi)-p.lo)+a.lo)/n)
}

func ddAddOne(a dd) dd {
	s := twoSum(1, a.hi)
	return quickTwoSum(s.hi, s.lo+a.lo)
}
