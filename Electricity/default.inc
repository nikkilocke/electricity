<script{{NonceAttribute}}>/**
 * Format a double number with up to 4 places
 * @param {number|string} number
 * @returns {string}
 */
Type.double.download = function(number) {
	if (number != null && number !== '') {
		number = parseFloat(number).toFixed(4);
		return number;
	}
	return '';
}
</script>

