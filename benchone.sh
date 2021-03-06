#!/bin/sh

# Number of times to run ProofGen, Z3, and GRASShopper per subject
NPG=10
NZ3=10
NGH=10

name=$1
path=$2
spl=$3
approx=$4
opt=$5

if [ -n "${spl}" ];
then
	mode="gh"
else
	mode="z3"
fi

TMPCVF="benchone.cvf.tmp"
TMPZ3="benchone.z3.tmp"
TMPAWK="benchone.awk.tmp"
TMPSPL="benchone.spl.tmp"
TMPGH="benchone.z3.gh"
rm -f $TMPCVF $TMPZ3 $TMPAWK $TMPSPL $TMPGH

#
# Zeroth pass: dump total and proof LoC into awk file
#

if [ -n "${spl}" ];
then
	cat "${path}" "${spl}" > "${TMPCVF}"
else
	cp "${path}" "${TMPCVF}"
fi

loc=$(wc -l "${TMPCVF}" | sed 's/^ *\([0-9]\{1,\}\).*/\1/')
printf "LOC:Starling %d\n" "${loc}" > ${TMPAWK}

# Now the same but filtering the CVF (not the SPL!) for proof lines

if [ -n "${spl}" ];
then
	awk -f ./benchmark-awk/proofOnly.awk "${path}" | cat - "${spl}" > "${TMPCVF}"
else
	awk -f ./benchmark-awk/proofOnly.awk "${path}" > "${TMPCVF}"
fi

ploc=$(wc -l "${TMPCVF}" | sed 's/^ *\([0-9]\{1,\}\).*/\1/')
printf "LOC:StarlingProof %d\n" "${ploc}" >> ${TMPAWK}

#
# First pass: get SMT results and format them
#

COUNT=3
for i in $(seq 1 ${COUNT});
do
	./starling.sh "${approx}" "${opt}" -Pphase-time,phase-working-set,phase-virtual "${path}" >> ${TMPZ3} 2>&1
done
awk -f ./benchmark-awk/parseZ3.awk -v count="${COUNT}" ${TMPZ3} >> ${TMPAWK}

#
# Second pass: call GRASShopper if needed
#

if [ "${mode}" = "gh" ];
then
	./starling.sh "${approx}" "${opt}" -sgrass "${path}" > "${TMPSPL}"

	# Get lines of GRASShopper code.
	gloc=$(wc -l "${TMPSPL}" | sed 's/^ *\([0-9]\{1,\}\).*/\1/')
	printf "LOC:GH %d\n" "${gloc}" >> ${TMPAWK}

	# Time GRASShopper.
	gatime=0
	for i in $(seq 1 ${COUNT});
	do
		gntime=$(/usr/bin/time -p ${GRASSHOPPER} ${TMPSPL} 2>&1 | grep "real" | sed 's/[a-zA-Z ]\{1,\}//')
		gatime=$(dc -e "2 k ${gatime} ${gntime} + p")
	done
	gtime=$(dc -e "2 k $gatime $COUNT / p")
	printf "Elapsed:GH %.2f\n" "${gtime}" >> ${TMPAWK}
fi

awk -f ./benchmark-awk/benchToLatex.awk -v mode="${mode}" "${TMPAWK}"
