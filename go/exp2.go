package agentexperience

import "math"

// exp2 is 2^x correctly rounded: what .NET's Math.Pow(2, x) gives where the platform's C library pow is correctly
// rounded (glibc, on the Linux the contract's fixtures are checked on). Go's math.Pow and math.Exp2 are within a few
// ulps but not correctly rounded (math.Pow(2, -803580.0/2592000) is 2 ulps above), and ranking orders records on the
// last bit of a score: contract 1.18's retrieval-policy/ranking-near-tie pins it.
//
// It splits x into an integer k and a fraction f in [-1/2, 1/2] (exactly), evaluates 2^f = e^(f ln 2) in double-double
// arithmetic (about 104 bits, so the one rounding at the end is correct unless 2^f lies within about 2^-100 of a
// midpoint between two doubles), and scales by 2^k. Below 2^-1022, where the result is subnormal, the scaling rounds a
// second time; such a value is 0 to every ranking score it enters.
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
	return math.Ldexp(sum.hi+sum.lo, int(k))
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
