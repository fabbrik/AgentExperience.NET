package agentexperience

// exp2 against 2^x correctly rounded (computed to 70 significant digits and rounded once), and against math.Pow,
// which it replaces in ranking because math.Pow is not correctly rounded (contract 1.18's ranking-near-tie).

import (
	"math"
	"testing"
)

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
}

func TestExp2IsCorrectlyRounded(t *testing.T) {
	for _, c := range exp2Golden {
		if got := exp2(c.x); got != c.want {
			t.Errorf("exp2(%v) = %v, want %v", c.x, got, c.want)
		}
	}
	// The near tie's input, where math.Pow is 2 ulps above the correctly rounded value.
	if x := -803580.0 / 2592000.0; math.Pow(2, x) == exp2(x) {
		t.Errorf("math.Pow(2, %v) is correctly rounded after all; the fixture's reason for exp2 is gone", x)
	}
}

func TestExp2Edges(t *testing.T) {
	for _, c := range []struct{ x, want float64 }{
		{0, 1},
		{math.Copysign(0, -1), 1},
		{1, 2},
		{-1074, math.SmallestNonzeroFloat64},
		{-1100, 0},
		{math.Inf(-1), 0},
		{1023, math.Ldexp(1, 1023)},
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
