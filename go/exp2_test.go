package agentexperience

// exp2 against 2^x correctly rounded, two ways: a fixed table, and a reference computed here with math/big at 320 bits
// over deterministic samples (reproducible: go test -run TestExp2AgainstABigFloatReference -exp2.samples=80000).

import (
	"flag"
	"math"
	"math/big"
	"math/rand/v2"
	"sync"
	"testing"
)

var exp2Samples = flag.Int("exp2.samples", 8000, "how many inputs TestExp2AgainstABigFloatReference checks")

// exp2Golden is 2^x correctly rounded, computed independently in Python (decimal, 70 to 120 significant digits, then
// float(Decimal), which rounds once, subnormals included): float((Decimal(x) * Decimal(2).ln()).exp()).
var exp2Golden = []struct{ x, want float64 }{
	{-0x1.3d76b54932710p-2, 0x1.9cfe73c778b00p-1},    // 2^-0.31002314814814813 = 0.8066288166995434
	{-0x1.7f8cb487042c0p-2, 0x1.8aed25318ed84p-1},    // 2^-0.3745601851851852 = 0.7713405249591436
	{-0x1.4740da740da74p-3, 0x1.ca51aafe3c769p-1},    // 2^-0.15979166666666667 = 0.8951543269488244
	{-0x1.0000000000000p-1, 0x1.6a09e667f3bcdp-1},    // 2^-0.5 = 0.7071067811865476
	{-0x1.0000000000000p+0, 0x1.0000000000000p-1},    // 2^-1.0 = 0.5
	{-0x1.19799812dea11p-40, 0x1.fffffffffe79dp-1},   // 2^-1e-12 = 0.9999999999993069
	{-0x1.501a7462c60e7p-1, 0x1.44d4e39abfeecp-1},    // 2^-0.6564518328259766 = 0.6344367147830474
	{-0x1.63775e53c31a1p+5, 0x1.7b2c6f71375d3p-45},   // 2^-44.433285383597884 = 4.209672338204587e-14
	{0x1.e000000000000p+1, 0x1.ae89f995ad3adp+3},     // 2^3.75 = 13.454342644059432
	{-0x1.f420000000000p+9, 0x1.ae89f995ad3adp-1001}, // 2^-1000.25 = 7.847780312860669e-302
	{-0x1.ec491682e977cp-2, 0x1.6ee70230b62e1p-1},    // 2^-0.48074755834601723 = 0.7166062054121448
	{-0x1.1d232bde4bfdbp-1, 0x1.5c091c85147c7p-1},    // 2^-0.5569089611872146 = 0.6797570145083619
	{-0x1.455eaf57abd5fp+1, 0x1.5fa99db06b3a6p-3},    // 2^-2.5419520547945207 = 0.1717102355445636
	{-0x1.3c188f77e66d5p+5, 0x1.670aaceadbad6p-40},   // 2^-39.51199239417989 = 1.2755723624392815e-12
	{-0x1.20b8935b332cdp-1, 0x1.5a59e7437c0eap-1},    // 2^-0.563908200152207 = 0.676467158305994
	{-0x1.8081fb4c14541p+4, 0x1.f4dc6475e43f9p-25},   // 2^-24.031733796296297 = 5.830788378438364e-08
	{-0x1.767696f1f7a80p+5, 0x1.2475ba9021135p-47},   // 2^-46.80790509259259 = 8.11739225768758e-15
	{-0x1.de4401ebecaeep+2, 0x1.70e74323f1895p-8},    // 2^-7.472900848765432 = 0.005629018680286664
	{-0x1.212937a48b59cp+5, 0x1.cf007d4151a2dp-37},   // 2^-36.14512566137566 = 1.3159305787040185e-11
	{-0x1.0d258a6dfc352p+7, 0x1.58193e4c0fe86p-135},  // 2^-134.57332175925927 = 3.0859830356812564e-41
	{-0x1.6540ddebbc99ap+5, 0x1.44c8203647e56p-45},   // 2^-44.65667328042328 = 3.605801674096303e-14
	{-0x1.694962fc962fdp+6, 0x1.99ac9a4fef420p-91},   // 2^-90.32166666666667 = 6.463519672443351e-28
	{-0x1.eddd14c03af2ap+3, 0x1.7b2fa95c3409ap-16},   // 2^-15.433237433862434 = 2.2601257442301204e-05
	{-0x1.5c4c30a5754adp+4, 0x1.2c897a62313fdp-22},   // 2^-21.768601080246913 = 2.7989691465626243e-07
	{-0x1.3525e0af1c02dp+7, 0x1.57f1125543866p-155},  // 2^-154.57397982804233 = 2.941680642062522e-47
	{-0x1.805eaad208e9cp-1, 0x1.3048decf3bdb3p-1},    // 2^-0.7507222539320142 = 0.5943059566470111
	{-0x1.1b21a40c81969p+1, 0x1.ba0a74a74c4fap-3},    // 2^-2.211964136225266 = 0.21584025512269261
	{-0x1.89ce5c5561d5ep+1, 0x1.e5855fec8c600p-4},    // 2^-3.0766101281075597 = 0.11853539915389177
	{-0x1.26ecc8d7de3e3p+0, 0x1.ccc8eb8917a21p-2},    // 2^-1.1520505454084222 = 0.44998519920582863
	{-0x1.fac0ca5c3ac83p-1, 0x1.01d32da0d48a1p-1},    // 2^-0.9897521245560629 = 0.5035642870737044
	{-0x1.8e85ed787120bp+6, 0x1.4aa958b270d07p-100},  // 2^-99.6307886904762 = 1.0189294446547113e-30
	{-0x1.632b4180e4db2p+5, 0x1.8511706efedb8p-45},   // 2^-44.396121031746034 = 4.3195238702578525e-14
	// Subnormal results, each rounded once, and large positive x.
	{-0x1.ff40000000000p+9, 0x0.b504f333f9de6p-1022},  // 2^-1022.5 = 1.5733648139913585e-308
	{-0x1.0193333333333p+10, 0x0.00cfefc5e672ap-1022}, // 2^-1030.3 = 7.0598498970396e-311
	{-0x1.06b0000000000p+10, 0x0.00000009837f0p-1022}, // 2^-1050.75 = 4.928696e-317
	{-0x1.0b86666666666p+10, 0x0.000000000000fp-1022}, // 2^-1070.1 = 7.4e-323
	{-0x1.0c7999999999ap+10, 0x0.0000000000001p-1022}, // 2^-1073.9 = 5e-324
	{-0x1.0ca0000000000p+10, 0x0.0000000000001p-1022}, // 2^-1074.5 = 5e-324
	{-0x1.0c90000000000p+10, 0x0.0000000000001p-1022}, // 2^-1074.25 = 5e-324
	{-0x1.0cb0000000000p+10, 0x0.0000000000001p-1022}, // 2^-1074.75 = 5e-324
	{-0x1.0ce0000000000p+10, 0x0.0p+0},                // 2^-1075.5 = 0.0
	{0x1.f440000000000p+9, 0x1.6a09e667f3bcdp+1000},   // 2^1000.5 = 1.5153420044823246e+301
	{0x1.ffa0000000000p+9, 0x1.306fe0a31b715p+1023},   // 2^1023.25 = 1.068914733284906e+308
	{0x1.ffffdf3b645a2p+9, 0x1.ffa52de61c1b3p+1023},   // 2^1023.999 = 1.796447500688184e+308
	{0x1.0010000000000p+9, 0x1.172b83c7d517bp+512},    // 2^512.125 = 1.4621318225692964e+154
	{0x1.5e26666666666p+9, 0x1.3b2c47bff827ap+700},    // 2^700.3 = 6.4759869286261e+210
}

func TestExp2IsCorrectlyRounded(t *testing.T) {
	for _, c := range exp2Golden {
		if got := exp2(c.x); got != c.want {
			t.Errorf("exp2(%v) = %v, want %v", c.x, got, c.want)
		}
	}
	// Why ranking does not use math.Pow: on the near tie's input it is off (2 ulps above, with Go 1.26). Logged, not
	// asserted, so a toolchain whose math.Pow improves does not fail the build.
	if x := -803580.0 / 2592000.0; math.Pow(2, x) != exp2(x) {
		t.Logf("math.Pow(2, %v) = %v, exp2 = %v", x, math.Pow(2, x), exp2(x))
	}
}

func TestExp2Edges(t *testing.T) {
	for _, c := range []struct{ x, want float64 }{
		{0, 1},
		{math.Copysign(0, -1), 1},
		{1, 2},
		{-1022, 0x1p-1022},
		{-1074, math.SmallestNonzeroFloat64},
		{-1075, 0}, // exactly halfway between 0 and 2^-1074: ties to even
		{math.Nextafter(-1075, 0), math.SmallestNonzeroFloat64},
		{math.Nextafter(-1075, math.Inf(-1)), 0},
		{-1080, 0},
		{-1100, 0},
		{math.Inf(-1), 0},
		{1023, 0x1p1023},
		{1024, math.Inf(1)},
		{1025, math.Inf(1)},
		{math.Inf(1), math.Inf(1)},
	} {
		if got := exp2(c.x); got != c.want {
			t.Errorf("exp2(%v) = %v, want %v", c.x, got, c.want)
		}
	}
	if !math.IsNaN(exp2(math.NaN())) {
		t.Error("exp2(NaN) is not NaN")
	}
}

func TestExp2AgainstABigFloatReference(t *testing.T) {
	random := rand.New(rand.NewPCG(28, 1))
	halfLives := []float64{604800, 1209600, 2592000, 7776000, 31536000}
	for i := range *exp2Samples {
		var x float64
		switch i % 6 {
		case 0: // decay within a few half-lives
			x = -random.Float64()
		case 1:
			x = -60 * random.Float64()
		case 2: // an age in whole seconds over a half-life, as ranking divides them
			x = -float64(random.IntN(1_000_000_000)+1) / halfLives[random.IntN(len(halfLives))]
		case 3: // subnormal results
			x = -1022 - 58*random.Float64()
		case 4: // normal results down to the smallest
			x = -1022 * random.Float64()
		default: // large results
			x = 1024 * random.Float64()
		}
		if got, want := exp2(x), exp2Reference(x); got != want {
			t.Fatalf("exp2(%v) = %v, want %v", x, got, want)
		}
	}
}

// exp2Reference is 2^x computed with math/big at 320 bits, by a different route from exp2's: e^(x ln 2) with the
// argument halved until it is tiny, a Taylor series, then squared back; big.Float's Float64 rounds the result once to
// the nearest double (ties to even), subnormals included.
func exp2Reference(x float64) float64 {
	const prec = 320
	y := new(big.Float).SetPrec(prec).SetFloat64(x)
	y.Mul(y, bigLn2Once())
	halvings := max(0, y.MantExp(nil)+8)
	z := new(big.Float).SetPrec(prec).SetMantExp(y, -halvings)
	sum := new(big.Float).SetPrec(prec).SetInt64(1)
	term := new(big.Float).SetPrec(prec).SetInt64(1)
	for n := int64(1); n < 200; n++ {
		term.Mul(term, z)
		term.Quo(term, new(big.Float).SetPrec(prec).SetInt64(n))
		sum.Add(sum, term)
		if term.Sign() == 0 || term.MantExp(nil) < -prec-8 {
			break
		}
	}
	for range halvings {
		sum.Mul(sum, sum)
	}
	result, _ := sum.Float64()
	return result
}

// bigLn2Once is bigLn2(320), computed once.
var bigLn2Once = sync.OnceValue(func() *big.Float { return bigLn2(320) })

// bigLn2 is ln 2 to prec bits: the sum of 1/(k 2^k) for k >= 1.
func bigLn2(prec uint) *big.Float {
	work := prec + 16
	one := new(big.Float).SetPrec(work).SetInt64(1)
	sum := new(big.Float).SetPrec(work)
	for k := 1; k <= int(work); k++ {
		denominator := new(big.Int).Lsh(big.NewInt(int64(k)), uint(k))
		term := new(big.Float).SetPrec(work).SetInt(denominator)
		sum.Add(sum, term.Quo(one, term))
	}
	return sum
}
