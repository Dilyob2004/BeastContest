const fs = require('fs');
const [a, b] = fs.readFileSync(0, 'utf8').trim().split(/\s+/).map(Number);
console.log(a + b);
